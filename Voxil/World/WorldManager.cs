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

    // === НОВЫЙ МЕТОД: ПОЛНАЯ ПЕРЕЗАГРУЗКА МИРА ===
    public void ReloadWorld()
    {
        Console.WriteLine("[World] Reloading...");

        // 1. Очищаем очереди генерации и физики
        _chunkGenerator.ClearQueue();
        _physicsBuilder.Clear();
        
        // Очищаем локальные очереди команд
        while (_incomingChunksToLoad.TryDequeue(out _)) { }
        while (_incomingChunksToUnload.TryDequeue(out _)) { }

        lock (_chunksLock)
        {
            // 2. Уничтожаем все чанки
            foreach (var chunk in _chunks.Values)
            {
                chunk.Dispose();
            }

            // 3. Очищаем все коллекции
            _chunks.Clear();
            _chunksInProgress.Clear();
            _activeChunkPositions.Clear();
            _staticToChunkMap.Clear();
        }

        // 4. Сбрасываем позицию последнего обновления, чтобы генерация началась сразу
        _lastChunkUpdatePos = new OpenTK.Mathematics.Vector3(float.MaxValue);
        _forceUpdate = true;

        Console.WriteLine("[World] Reload complete.");
    }

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
        float distSq = (playerPos - _lastChunkUpdatePos).LengthSquared;
        if (distSq < (Constants.ChunkSizeWorld * 2.0f) * (Constants.ChunkSizeWorld * 2.0f) && !_forceUpdate) return;

        _lastChunkUpdatePos = playerPos;
        _forceUpdate = false;

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

        // Если высота мира ограничена, используем это
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
            long dxA = a.X - center.X; long dzA = a.Z - center.Z;
            long distA = dxA * dxA + dzA * dzA;
            
            long dxB = b.X - center.X; long dzB = b.Z - center.Z;
            long distB = dxB * dxB + dzB * dzB;
            
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
        long maxTicks = Stopwatch.Frequency / 1000 * 3; // 3ms budget
        long startTicks = Stopwatch.GetTimestamp();

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

            if (Stopwatch.GetTimestamp() - startTicks > maxTicks) break;
        }
    }

    private void ProcessPhysicsResults()
    {
        long maxTicks = Stopwatch.Frequency / 1000 * 2; // 2ms budget
        long startTicks = Stopwatch.GetTimestamp();

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

            if (Stopwatch.GetTimestamp() - startTicks > maxTicks) break;
        }
    }

    // ... (Остальные методы без изменений) ...
    public void RebuildPhysics(Chunk chunk) { if (chunk == null || !chunk.IsLoaded) return; _physicsBuilder.EnqueueTask(chunk, urgent: true); }
    public void CreateDetachedObject(List<Vector3i> globalCluster) { /* ... копия старого кода ... */ 
        foreach (var pos in globalCluster) RemoveVoxelGlobal(pos);
        OpenTK.Mathematics.Vector3 minIndex = new OpenTK.Mathematics.Vector3(float.MaxValue);
        foreach (var v in globalCluster) minIndex = OpenTK.Mathematics.Vector3.ComponentMin(minIndex, new OpenTK.Mathematics.Vector3(v.X, v.Y, v.Z));
        List<Vector3i> localVoxels = new List<Vector3i>();
        Vector3i minIdxInt = new Vector3i((int)minIndex.X, (int)minIndex.Y, (int)minIndex.Z);
        foreach (var v in globalCluster) localVoxels.Add(v - minIdxInt);
        System.Numerics.Vector3 worldPos = (minIndex * Constants.VoxelSize).ToSystemNumerics();
        _objectsCreationQueue.Enqueue(new VoxelObjectCreationData { Voxels = localVoxels, Material = MaterialType.Stone, WorldPosition = worldPos });
    }
    private void ProcessNewDebris() { /* ... копия старого кода ... */ 
        while (_objectsCreationQueue.TryDequeue(out var data)) {
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
    public void ProcessVoxelObjects() { foreach (var vo in _voxelObjects) { if (PhysicsWorld.Simulation.Bodies.BodyExists(vo.BodyHandle)) { var pose = PhysicsWorld.GetPose(vo.BodyHandle); vo.UpdatePose(pose); } } }
    public void QueueForRemoval(VoxelObject obj) => _objectsToRemove.Add(obj);
    private void ProcessRemovals() { foreach (var obj in _objectsToRemove) { try { _bodyToVoxelObjectMap.Remove(obj.BodyHandle); _voxelObjects.Remove(obj); PhysicsWorld.RemoveBody(obj.BodyHandle); obj.Dispose(); } catch { } } _objectsToRemove.Clear(); }
    public void DestroyVoxelAt(CollidableReference collidable, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal) { /* ... копия старого кода ... */ 
         var pointInside = worldHitLocation - worldHitNormal * (Constants.VoxelSize * 0.5f);
        if (collidable.Mobility == CollidableMobility.Static) {
            Vector3i globalVoxelIndex = new Vector3i((int)Math.Floor(pointInside.X / Constants.VoxelSize), (int)Math.Floor(pointInside.Y / Constants.VoxelSize), (int)Math.Floor(pointInside.Z / Constants.VoxelSize));
            if (RemoveVoxelGlobal(globalVoxelIndex)) { NotifyVoxelFastDestroyed(globalVoxelIndex); _integritySystem.QueueCheck(globalVoxelIndex); }
        } else if (collidable.Mobility == CollidableMobility.Dynamic && _bodyToVoxelObjectMap.TryGetValue(collidable.BodyHandle, out var voxelObj)) {
            Matrix4 model = Matrix4.CreateTranslation(-voxelObj.LocalCenterOfMass) * Matrix4.CreateFromQuaternion(voxelObj.Rotation) * Matrix4.CreateTranslation(voxelObj.Position);
            Matrix4 invModel = Matrix4.Invert(model);
            Vector4 localHitMeters = new Vector4(pointInside.ToOpenTK(), 1.0f) * invModel;
            Vector3i localVoxelIndex = new Vector3i((int)Math.Floor(localHitMeters.X / Constants.VoxelSize), (int)Math.Floor(localHitMeters.Y / Constants.VoxelSize), (int)Math.Floor(localHitMeters.Z / Constants.VoxelSize));
            if (voxelObj.RemoveVoxel(localVoxelIndex)) {
                if (voxelObj.VoxelCoordinates.Count == 0) return;
                var clusters = voxelObj.GetConnectedClusters();
                if (clusters.Count == 1) { voxelObj.RebuildMeshAndPhysics(PhysicsWorld); }
                else {
                    clusters.Sort((a, b) => b.Count.CompareTo(a.Count));
                    var mainCluster = clusters[0];
                    var mainSet = new HashSet<Vector3i>(mainCluster);
                    voxelObj.VoxelCoordinates.RemoveAll(v => !mainSet.Contains(v));
                    voxelObj.RebuildMeshAndPhysics(PhysicsWorld);
                    for (int i = 1; i < clusters.Count; i++) SpawnSplitCluster(clusters[i], voxelObj);
                }
            }
        }
    }
    private void SpawnSplitCluster(List<Vector3i> localClusterVoxels, VoxelObject parentObj) { /* ... копия старого кода ... */ 
        Vector3i anchorIdx = localClusterVoxels[0];
        List<Vector3i> newLocalVoxels = new List<Vector3i>(); foreach(var v in localClusterVoxels) newLocalVoxels.Add(v - anchorIdx);
        System.Numerics.Vector3 calculatedLocalCoM = System.Numerics.Vector3.Zero; foreach (var v in newLocalVoxels) calculatedLocalCoM += (v.ToSystemNumerics() + new System.Numerics.Vector3(0.5f)) * Constants.VoxelSize;
        if (newLocalVoxels.Count > 0) calculatedLocalCoM /= newLocalVoxels.Count;
        System.Numerics.Vector3 anchorPosInParent = (anchorIdx.ToSystemNumerics() + new System.Numerics.Vector3(0.5f)) * Constants.VoxelSize;
        anchorPosInParent -= parentObj.LocalCenterOfMass.ToSystemNumerics();
        System.Numerics.Vector3 anchorWorldPos = parentObj.Position.ToSystemNumerics() + System.Numerics.Vector3.Transform(anchorPosInParent, parentObj.Rotation.ToSystemNumerics());
        System.Numerics.Vector3 anchorInNewLocal = new System.Numerics.Vector3(0.5f) * Constants.VoxelSize;
        System.Numerics.Vector3 offset = anchorInNewLocal - calculatedLocalCoM;
        System.Numerics.Vector3 rotatedOffset = System.Numerics.Vector3.Transform(offset, parentObj.Rotation.ToSystemNumerics());
        System.Numerics.Vector3 finalSpawnPos = anchorWorldPos - rotatedOffset;
        _objectsCreationQueue.Enqueue(new VoxelObjectCreationData { Voxels = newLocalVoxels, Material = parentObj.Material, WorldPosition = finalSpawnPos });
    }
    private bool RemoveVoxelGlobal(Vector3i globalVoxelIndex) { /* ... копия старого кода ... */ 
        Vector3i chunkPos = GetChunkPosFromVoxelIndex(globalVoxelIndex);
        if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded) {
            int res = Constants.ChunkResolution;
            int lx = globalVoxelIndex.X % res; if(lx < 0) lx += res;
            int ly = globalVoxelIndex.Y % res; if(ly < 0) ly += res;
            int lz = globalVoxelIndex.Z % res; if(lz < 0) lz += res;
            return chunk.RemoveVoxelAndUpdate(new Vector3i(lx, ly, lz));
        } return false;
    }
    private Vector3i GetChunkPosFromVoxelIndex(Vector3i voxelIndex) => new Vector3i((int)Math.Floor((float)voxelIndex.X / Constants.ChunkResolution), (int)Math.Floor((float)voxelIndex.Y / Constants.ChunkResolution), (int)Math.Floor((float)voxelIndex.Z / Constants.ChunkResolution));
    public bool IsVoxelSolidGlobal(Vector3i globalVoxelIndex) { /* ... копия старого кода ... */ 
        Vector3i chunkPos = GetChunkPosFromVoxelIndex(globalVoxelIndex);
        if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded) {
            int res = Constants.ChunkResolution;
            int lx = globalVoxelIndex.X % res; if (lx < 0) lx += res;
            int ly = globalVoxelIndex.Y % res; if (ly < 0) ly += res;
            int lz = globalVoxelIndex.Z % res; if (lz < 0) lz += res;
            return chunk.IsVoxelSolidAt(new Vector3i(lx, ly, lz));
        } return false;
    }
    public bool IsChunkLoadedAt(Vector3i globalVoxelIndex) { Vector3i chunkPos = GetChunkPosFromVoxelIndex(globalVoxelIndex); return _chunks.ContainsKey(chunkPos) && _chunks[chunkPos].IsLoaded; }
    public void NotifyVoxelFastDestroyed(Vector3i worldPos) => OnVoxelFastDestroyed?.Invoke(worldPos);
    public void NotifyChunkModified(Chunk chunk) => OnChunkModified?.Invoke(chunk);
    public void RegisterVoxelObject(BodyHandle handle, VoxelObject obj) { lock (_chunksLock) _bodyToVoxelObjectMap[handle] = obj; }
    public void RegisterChunkStatic(StaticHandle handle, Chunk chunk) { lock (_chunksLock) _staticToChunkMap[handle] = chunk; }
    public void UnregisterChunkStatic(StaticHandle handle) { lock (_chunksLock) _staticToChunkMap.Remove(handle); }
    public OpenTK.Mathematics.Vector3 GetPlayerPosition() => PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle).Pose.Position.ToOpenTK();
    public List<Chunk> GetChunksSnapshot() { lock (_chunksLock) return new List<Chunk>(_chunks.Values); }
    public float GetViewRangeInMeters() => GameSettings.RenderDistance * Constants.ChunkSizeWorld;
    public void Dispose() { _isDisposed = true; _chunkGenerator.Dispose(); _physicsBuilder.Dispose(); _integritySystem.Dispose(); lock (_chunksLock) { foreach (var chunk in _chunks.Values) chunk.Dispose(); _chunks.Clear(); } foreach(var vo in _voxelObjects) vo.Dispose(); _voxelObjects.Clear(); }
    public void SpawnTestVoxel(System.Numerics.Vector3 position, MaterialType material) { var voxels = new List<Vector3i> { new Vector3i(0, 0, 0) }; _objectsCreationQueue.Enqueue(new VoxelObjectCreationData { Voxels = voxels, Material = material, WorldPosition = position }); }
}