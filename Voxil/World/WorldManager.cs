using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepuVector3 = System.Numerics.Vector3;

public class WorldManager : IDisposable
{
    public PhysicsWorld PhysicsWorld { get; }
    private readonly PlayerController _playerController;
    private readonly AsyncChunkGenerator _chunkGenerator;
    private readonly AsyncChunkPhysics _physicsBuilder;
    private readonly StructuralIntegritySystem _integritySystem;

    private readonly Dictionary<Vector3i, Chunk> _chunks = new();
    private readonly Dictionary<BodyHandle, VoxelObject> _bodyToVoxelObjectMap = new();
    private readonly Dictionary<StaticHandle, Chunk> _staticToChunkMap = new();
    private readonly List<VoxelObject> _voxelObjects = new();

    private readonly ConcurrentQueue<VoxelObjectCreationData> _objectsCreationQueue = new();
    private readonly List<VoxelObject> _objectsToRemove = new();

    private readonly ConcurrentQueue<List<ChunkGenerationTask>> _incomingChunksToLoad = new();
    private readonly ConcurrentQueue<List<Vector3i>> _incomingChunksToUnload = new();

    private readonly object _chunksLock = new();
    private readonly HashSet<Vector3i> _chunksInProgress = new();
    private readonly HashSet<Vector3i> _activeChunkPositions = new();

    private OpenTK.Mathematics.Vector3 _lastChunkUpdatePos = new(float.MaxValue);
    private bool _forceUpdate = false;
    private volatile bool _isChunkUpdateRunning = false;
    private bool _isDisposed = false;

    private readonly HashSet<Vector3i> _requiredChunksSet;
    private readonly List<ChunkGenerationTask> _chunksToLoadList;
    private readonly List<Vector3i> _chunksToUnloadList;
    private readonly List<Vector3i> _sortedLoadPositions;

    public event Action<Chunk> OnChunkLoaded;
    public event Action<Chunk> OnChunkModified;
    public event Action<Vector3i> OnChunkUnloaded;
    public event Action<Vector3i> OnVoxelFastDestroyed;

    public const int WorldHeightChunks = 16;
    private float _memoryLogTimer = 0f;

    private struct VoxelObjectCreationData
    {
        public List<Vector3i> Voxels;
        public MaterialType Material;
        public System.Numerics.Vector3 WorldPosition;
    }

    public Dictionary<Vector3i, Chunk> GetAllChunks() => _chunks;
    public List<VoxelObject> GetAllVoxelObjects() => _voxelObjects;

    public WorldManager(PhysicsWorld physicsWorld, PlayerController playerController)
    {
        PhysicsWorld = physicsWorld;
        _playerController = playerController;

        _requiredChunksSet = new HashSet<Vector3i>(50000);
        _chunksToLoadList = new List<ChunkGenerationTask>(10000);
        _chunksToUnloadList = new List<Vector3i>(10000);
        _sortedLoadPositions = new List<Vector3i>(50000);

        _chunkGenerator = new AsyncChunkGenerator(12345, GameSettings.GenerationThreads);
        _physicsBuilder = new AsyncChunkPhysics();
        _integritySystem = new StructuralIntegritySystem(this);
    }

    public void SetGenerationThreadCount(int count) => _chunkGenerator.SetThreadCount(count);

    public void Update(float deltaTime)
    {
        UpdateVisibleChunks();
        ProcessGeneratedChunks();
        ProcessPhysicsResults();
        ProcessNewDebris();
        ProcessVoxelObjects();
        ProcessRemovals();

        _memoryLogTimer += deltaTime;
        if (_memoryLogTimer >= 5.0f)
        {
            Console.WriteLine($"[World] Chunks: {_chunks.Count}, Dynamic Objects: {_voxelObjects.Count}");
            _memoryLogTimer = 0f;
        }
    }

    private void UpdateVisibleChunks()
    {
        ApplyChunkUpdates();

        if (_isChunkUpdateRunning) return;

        var playerPos = GetPlayerPosition();
        // Используем ChunkSizeWorld (16м)
        float distSq = (playerPos - _lastChunkUpdatePos).LengthSquared;
        if (distSq < (Constants.ChunkSizeWorld * 2.0f) * (Constants.ChunkSizeWorld * 2.0f) && !_forceUpdate) return;

        _lastChunkUpdatePos = playerPos;
        _forceUpdate = false;

        // Делим на 16, чтобы получить координаты чанка
        var pX = (int)Math.Floor(playerPos.X / Constants.ChunkSizeWorld);
        var pZ = (int)Math.Floor(playerPos.Z / Constants.ChunkSizeWorld);
        var currentCenter = new Vector3i(pX, 0, pZ);

        _isChunkUpdateRunning = true;

        HashSet<Vector3i> activeSnapshot;
        lock (_chunksLock) { activeSnapshot = new HashSet<Vector3i>(_activeChunkPositions); }

        int viewDist = GameSettings.RenderDistance;
        int height = WorldHeightChunks;

        Task.Run(() =>
        {
            try { CalculateChunksBackground(currentCenter, activeSnapshot, viewDist, height); }
            finally { _isChunkUpdateRunning = false; }
        });
    }

    private void CalculateChunksBackground(Vector3i center, HashSet<Vector3i> activeSnapshot, int viewDist, int height)
    {
        _chunksToLoadList.Clear();
        _chunksToUnloadList.Clear();
        _requiredChunksSet.Clear();
        _sortedLoadPositions.Clear();

        for (int x = -viewDist; x <= viewDist; x++)
        {
            for (int z = -viewDist; z <= viewDist; z++)
            {
                for (int y = 0; y < height; y++) 
                    _requiredChunksSet.Add(new Vector3i(center.X + x, y, center.Z + z));
            }
        }

        foreach (var pos in activeSnapshot) if (!_requiredChunksSet.Contains(pos)) _chunksToUnloadList.Add(pos);
        foreach (var pos in _requiredChunksSet) if (!activeSnapshot.Contains(pos)) _sortedLoadPositions.Add(pos);

        _sortedLoadPositions.Sort((a, b) =>
        {
            int dxA = a.X - center.X; int dzA = a.Z - center.Z;
            int distA = dxA * dxA + dzA * dzA;
            
            int dxB = b.X - center.X; int dzB = b.Z - center.Z;
            int distB = dxB * dxB + dzB * dzB; // <--- Вот эту строку я пропустил
            
            return distA.CompareTo(distB);
        });

        foreach (var pos in _sortedLoadPositions)
        {
            int dx = pos.X - center.X; int dz = pos.Z - center.Z;
            int priority = dx * dx + dz * dz;
            _chunksToLoadList.Add(new ChunkGenerationTask(pos, priority));
        }

        if (_chunksToUnloadList.Count > 0) _incomingChunksToUnload.Enqueue(new List<Vector3i>(_chunksToUnloadList));
        if (_chunksToLoadList.Count > 0) _incomingChunksToLoad.Enqueue(new List<ChunkGenerationTask>(_chunksToLoadList));
    }

    private void ApplyChunkUpdates()
    {
        while (_incomingChunksToUnload.TryDequeue(out var unloadList))
        {
            foreach (var pos in unloadList) UnloadChunk(pos);
        }

        while (_incomingChunksToLoad.TryDequeue(out var loadList))
        {
            foreach (var task in loadList)
            {
                if (!_activeChunkPositions.Contains(task.Position) && !_chunksInProgress.Contains(task.Position))
                {
                    _chunksInProgress.Add(task.Position);
                    _activeChunkPositions.Add(task.Position);
                    _chunkGenerator.EnqueueTask(task.Position, task.Priority);
                }
            }
        }
    }

    private void UnloadChunk(Vector3i position)
    {
        lock (_chunksLock)
        {
            if (_chunks.TryGetValue(position, out var chunk))
            {
                chunk.Dispose();
                _chunks.Remove(position);
                OnChunkUnloaded?.Invoke(position);
            }
        }
        _chunksInProgress.Remove(position);
        _activeChunkPositions.Remove(position);
    }

    private void ProcessGeneratedChunks()
    {
        while (_chunkGenerator.TryGetResult(out var result))
        {
            if (!_activeChunkPositions.Contains(result.Position))
            {
                System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels);
                continue;
            }

            Chunk chunkToAdd = null;
            lock (_chunksLock)
            {
                if (!_chunks.ContainsKey(result.Position))
                {
                    chunkToAdd = new Chunk(result.Position, this);
                    chunkToAdd.SetDataFromArray(result.Voxels);
                    _chunks[result.Position] = chunkToAdd;
                }
            }

            System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels);

            if (chunkToAdd != null)
            {
                OnChunkLoaded?.Invoke(chunkToAdd);
                _physicsBuilder.EnqueueTask(chunkToAdd);
            }
        }
    }

    private void ProcessPhysicsResults()
    {
        while (_physicsBuilder.TryGetResult(out var result))
        {
            if (!result.IsValid) continue;

            using (result.Data)
            {
                if (result.TargetChunk == null || !result.TargetChunk.IsLoaded) continue;

                StaticHandle handle = default;
                if (result.Data.CollidersArray != null && result.Data.Count > 0)
                {
                    handle = PhysicsWorld.AddStaticChunkBody(
                        (result.TargetChunk.Position * Constants.ChunkSizeWorld).ToSystemNumerics(),
                        result.Data.CollidersArray,
                        result.Data.Count
                    );
                }
                result.TargetChunk.OnPhysicsRebuilt(handle);
            }
        }
    }

    public void RebuildPhysics(Chunk chunk)
    {
        if (chunk == null || !chunk.IsLoaded) return;
        _physicsBuilder.EnqueueTask(chunk, urgent: true);
    }

    public void CreateDetachedObject(List<Vector3i> globalCluster)
    {
        foreach (var pos in globalCluster) RemoveVoxelGlobal(pos);

        OpenTK.Mathematics.Vector3 min = new OpenTK.Mathematics.Vector3(float.MaxValue);
        foreach (var v in globalCluster)
            min = OpenTK.Mathematics.Vector3.ComponentMin(min, new OpenTK.Mathematics.Vector3(v.X, v.Y, v.Z));

        List<Vector3i> localVoxels = new List<Vector3i>();
        foreach (var v in globalCluster)
            localVoxels.Add(v - new Vector3i((int)min.X, (int)min.Y, (int)min.Z));

        _objectsCreationQueue.Enqueue(new VoxelObjectCreationData
        {
            Voxels = localVoxels,
            Material = MaterialType.Stone,
            WorldPosition = min.ToSystemNumerics()
        });
    }

    private void ProcessNewDebris()
    {
        while (_objectsCreationQueue.TryDequeue(out var data))
        {
            var vo = new VoxelObject(data.Voxels, data.Material);
            vo.OnEmpty += QueueForRemoval;
            var handle = PhysicsWorld.CreateVoxelObjectBody(data.Voxels, data.Material, data.WorldPosition, out var com);
            
            var realPos = data.WorldPosition + com;
            var bodyRef = PhysicsWorld.Simulation.Bodies.GetBodyReference(handle);
            bodyRef.Pose.Position = realPos;

            vo.InitializePhysics(handle, com.ToOpenTK());

            _voxelObjects.Add(vo);
            RegisterVoxelObject(handle, vo);
        }
    }

    public void ProcessVoxelObjects()
    {
        foreach (var vo in _voxelObjects)
        {
            if (PhysicsWorld.Simulation.Bodies.BodyExists(vo.BodyHandle))
            {
                var pose = PhysicsWorld.GetPose(vo.BodyHandle);
                vo.UpdatePose(pose);
            }
        }
    }

    public void QueueForRemoval(VoxelObject obj) => _objectsToRemove.Add(obj);

    private void ProcessRemovals()
    {
        foreach (var obj in _objectsToRemove)
        {
            try
            {
                _bodyToVoxelObjectMap.Remove(obj.BodyHandle);
                _voxelObjects.Remove(obj);
                PhysicsWorld.RemoveBody(obj.BodyHandle);
                obj.Dispose();
            }
            catch { }
        }
        _objectsToRemove.Clear();
    }

    public void DestroyVoxelAt(CollidableReference collidable, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        var pointInside = worldHitLocation - worldHitNormal * 0.05f;
        
        // ВАЖНО: Тут логика для вокселей 0.25м сложнее.
        // Пока оставляем как есть, но физика разрушений будет баговать, пока не перепишем рейкаст под микро-воксели.
        Vector3i globalPos = new Vector3i(
            (int)Math.Floor(pointInside.X),
            (int)Math.Floor(pointInside.Y),
            (int)Math.Floor(pointInside.Z));

        if (collidable.Mobility == CollidableMobility.Static)
        {
            if (RemoveVoxelGlobal(globalPos))
            {
                NotifyVoxelFastDestroyed(globalPos);
                _integritySystem.QueueCheck(globalPos);
            }
        }
        else if (collidable.Mobility == CollidableMobility.Dynamic &&
                 _bodyToVoxelObjectMap.TryGetValue(collidable.BodyHandle, out var voxelObj))
        {
            Matrix4 model = Matrix4.CreateTranslation(-voxelObj.LocalCenterOfMass) *
                            Matrix4.CreateFromQuaternion(voxelObj.Rotation) *
                            Matrix4.CreateTranslation(voxelObj.Position);
            Matrix4 invModel = Matrix4.Invert(model);

            Vector4 localHit4 = new Vector4(pointInside.ToOpenTK(), 1.0f) * invModel;
            Vector3i localVoxel = new Vector3i(
                (int)Math.Floor(localHit4.X),
                (int)Math.Floor(localHit4.Y),
                (int)Math.Floor(localHit4.Z));

            if (voxelObj.RemoveVoxel(localVoxel))
            {
                voxelObj.RebuildMeshAndPhysics(PhysicsWorld);
            }
        }
    }

    private bool RemoveVoxelGlobal(Vector3i globalPos)
    {
        Vector3i chunkPos = GetChunkPosFromGlobal(globalPos);
        if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded)
        {
            // Здесь координаты нужно переводить аккуратно, пока оставим старую логику, но с новыми константами
            Vector3i localPos = globalPos - (chunkPos * Constants.ChunkSizeWorld);
            return chunk.RemoveVoxelAndUpdate(localPos);
        }
        return false;
    }

    private Vector3i GetChunkPosFromGlobal(Vector3i p) => new Vector3i(
        (int)Math.Floor((float)p.X / Constants.ChunkSizeWorld),
        (int)Math.Floor((float)p.Y / Constants.ChunkSizeWorld),
        (int)Math.Floor((float)p.Z / Constants.ChunkSizeWorld));

    public bool IsVoxelSolidGlobal(Vector3i globalPos)
    {
        var chunkPos = GetChunkPosFromGlobal(globalPos);
        if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded)
        {
            var local = globalPos - chunkPos * Constants.ChunkSizeWorld;
            return chunk.IsVoxelSolidAt(local);
        }
        return false;
    }

    public bool IsChunkLoadedAt(Vector3i globalPos)
    {
        var chunkPos = GetChunkPosFromGlobal(globalPos);
        return _chunks.ContainsKey(chunkPos) && _chunks[chunkPos].IsLoaded;
    }

    public void NotifyVoxelFastDestroyed(Vector3i worldPos) => OnVoxelFastDestroyed?.Invoke(worldPos);
    public void NotifyChunkModified(Chunk chunk) => OnChunkModified?.Invoke(chunk);

    public void RegisterVoxelObject(BodyHandle handle, VoxelObject obj)
    {
        lock (_chunksLock) _bodyToVoxelObjectMap[handle] = obj;
    }

    public void RegisterChunkStatic(StaticHandle handle, Chunk chunk)
    {
        lock (_chunksLock) _staticToChunkMap[handle] = chunk;
    }

    public void UnregisterChunkStatic(StaticHandle handle)
    {
        lock (_chunksLock) _staticToChunkMap.Remove(handle);
    }

    public OpenTK.Mathematics.Vector3 GetPlayerPosition() => 
        PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle).Pose.Position.ToOpenTK();

    public List<Chunk> GetChunksSnapshot()
    {
        lock (_chunksLock) return new List<Chunk>(_chunks.Values);
    }

    // Возвращаем метры для шейдера
    public float GetViewRangeInMeters() => GameSettings.RenderDistance * Constants.ChunkSizeWorld;

    public void Dispose()
    {
        _isDisposed = true;
        _chunkGenerator.Dispose();
        _physicsBuilder.Dispose();
        _integritySystem.Dispose();

        lock (_chunksLock)
        {
            foreach (var chunk in _chunks.Values) chunk.Dispose();
            _chunks.Clear();
        }
        
        foreach(var vo in _voxelObjects) vo.Dispose();
        _voxelObjects.Clear();
    }
}