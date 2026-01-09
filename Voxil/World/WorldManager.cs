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

    // Состояние мира
    private readonly Dictionary<Vector3i, Chunk> _chunks = new();
    private readonly Dictionary<BodyHandle, VoxelObject> _bodyToVoxelObjectMap = new();
    private readonly Dictionary<StaticHandle, Chunk> _staticToChunkMap = new();
    private readonly List<VoxelObject> _voxelObjects = new();
    private readonly ConcurrentQueue<VoxelObjectCreationData> _objectsCreationQueue = new();
    private readonly List<VoxelObject> _objectsToRemove = new();

    // Планировщик
    private Thread _schedulerThread;
    private CancellationTokenSource _schedulerCts = new CancellationTokenSource();
    private readonly ConcurrentQueue<Vector3i> _unloadQueue = new();
    
    private readonly object _chunksLock = new();
    // InProgress гарантирует, что мы не добавим одну задачу дважды
    private readonly HashSet<Vector3i> _chunksInProgress = new();

    private Vector3i _currentPlayerChunkPos = new(int.MaxValue);
    private readonly object _playerPosLock = new object();
    private volatile bool _positionChangedDirty = false;
    
    private bool _isDisposed = false;

    public event Action<Chunk> OnChunkLoaded;
    public event Action<Chunk> OnChunkModified;
    public event Action<Vector3i> OnChunkUnloaded;
    public event Action<Vector3i> OnVoxelFastDestroyed;

    public const int WorldHeightChunks = 16;
    private float _memoryLogTimer = 0f;

    private struct VoxelObjectCreationData { public List<Vector3i> Voxels; public MaterialType Material; public System.Numerics.Vector3 WorldPosition; }
    public Dictionary<Vector3i, Chunk> GetAllChunks() => _chunks;
    public List<VoxelObject> GetAllVoxelObjects() => _voxelObjects;

    public WorldManager(PhysicsWorld physicsWorld, PlayerController playerController)
    {
        PhysicsWorld = physicsWorld;
        _playerController = playerController;
        _chunkGenerator = new AsyncChunkGenerator(12345, GameSettings.GenerationThreads);
        _physicsBuilder = new AsyncChunkPhysics();
        _integritySystem = new StructuralIntegritySystem(this);

        _schedulerThread = new Thread(SchedulerLoop)
        {
            IsBackground = true,
            Name = "WorldScheduler",
            Priority = ThreadPriority.AboveNormal 
        };
        _schedulerThread.Start();
    }
    
    public void SetGenerationThreadCount(int count) => _chunkGenerator.SetThreadCount(count);

    public void ReloadWorld()
    {
        Console.WriteLine("[World] Reloading...");
        _chunkGenerator.ClearQueue();
        _physicsBuilder.Clear();
        
        while (_unloadQueue.TryDequeue(out _)) { }

        lock (_chunksLock)
        {
            foreach (var chunk in _chunks.Values) chunk.Dispose();
            _chunks.Clear();
            _chunksInProgress.Clear();
            _staticToChunkMap.Clear();
        }

        lock (_playerPosLock)
        {
            _currentPlayerChunkPos = new(int.MaxValue);
            _positionChangedDirty = true;
        }
        Console.WriteLine("[World] Reload complete.");
    }

    public void Update(float deltaTime)
    {
        var playerPos = GetPlayerPosition();
        var currentChunkPos = new Vector3i((int)Math.Floor(playerPos.X / Constants.ChunkSizeWorld), 0, (int)Math.Floor(playerPos.Z / Constants.ChunkSizeWorld));
        
        lock (_playerPosLock)
        {
            if (currentChunkPos != _currentPlayerChunkPos)
            {
                _currentPlayerChunkPos = currentChunkPos;
                _positionChangedDirty = true;
                // Мы НЕ очищаем очередь принудительно.
                // Мы полагаемся на то, что очередь короткая (см. SchedulerLoop).
            }
        }

        ApplyUnloads();
        
        ProcessGeneratedChunks();
        ProcessPhysicsResults();
        
        ProcessNewDebris();
        ProcessVoxelObjects();
        ProcessRemovals();

        _memoryLogTimer += deltaTime;
        if (_memoryLogTimer >= 5.0f)
        {
            _memoryLogTimer = 0f;
        }
    }
    
    private void SchedulerLoop()
    {
        Vector3i lastScheduledCenter = new(int.MaxValue);

        while (!_schedulerCts.IsCancellationRequested)
        {
            Vector3i center;
            lock(_playerPosLock) center = _currentPlayerChunkPos;
            
            if (center != lastScheduledCenter)
            {
                lastScheduledCenter = center;
                ScheduleUnloads();
            }

            int viewDist = GameSettings.RenderDistance;
            bool dirty = false;

            // Цикл от ближних к дальним (R = 0, 1, 2...)
            for (int r = 0; r <= viewDist; r++)
            {
                if (_positionChangedDirty) { dirty = true; break; }

                // --- САМОЕ ВАЖНОЕ ИЗМЕНЕНИЕ ---
                // Мы не даем планировщику забить очередь тысячами дальних чанков.
                // Если в очереди уже есть 100 задач, мы ждем.
                // Как только вы сдвинетесь, _positionChangedDirty станет true, этот цикл прервется,
                // и планировщик снова начнет добавлять задачи с R=0 (самые близкие).
                // Так как очередь короткая (всего 100), новые задачи R=0 сразу попадут в работу.
                while (_chunkGenerator.PendingCount > 100) 
                {
                    if (_positionChangedDirty || _schedulerCts.IsCancellationRequested) { dirty = true; break; }
                    Thread.Sleep(2);
                }
                if (dirty) break;

                ProcessRing(center, r, WorldHeightChunks);
            }
            
            if (_positionChangedDirty)
            {
                lock(_playerPosLock) _positionChangedDirty = false;
                // Немедленный перезапуск
            }
            else
            {
                Thread.Sleep(20);
            }
        }
    }

    private void ScheduleUnloads()
    {
        Vector3i center;
        lock (_playerPosLock) center = _currentPlayerChunkPos;
        int viewDist = GameSettings.RenderDistance;
        int safeUnloadDistSq = (viewDist + 2) * (viewDist + 2);

        lock(_chunksLock)
        {
            foreach(var pos in _chunks.Keys)
            {
                int dx = pos.X - center.X;
                int dz = pos.Z - center.Z;
                if (dx * dx + dz * dz > safeUnloadDistSq) 
                {
                    _unloadQueue.Enqueue(pos);
                }
            }
        }
    }

    private void ProcessRing(Vector3i center, int radius, int height)
    {
        if (radius == 0)
        {
            for (int y = 0; y < height; y++) TrySchedule(new Vector3i(center.X, y, center.Z));
            return;
        }
        
        for (int i = -radius; i <= radius; i++)
        {
            for (int y = 0; y < height; y++)
            {
                TrySchedule(new Vector3i(center.X + i, y, center.Z + radius));
                TrySchedule(new Vector3i(center.X + i, y, center.Z - radius));
                if (i > -radius && i < radius)
                {
                    TrySchedule(new Vector3i(center.X + radius, y, center.Z + i));
                    TrySchedule(new Vector3i(center.X - radius, y, center.Z + i));
                }
            }
        }
    }
    
    private void TrySchedule(Vector3i pos)
    {
        lock (_chunksLock)
        {
            if (_chunks.ContainsKey(pos)) return;
            if (_chunksInProgress.Contains(pos)) return;
            _chunksInProgress.Add(pos);
        }

        Vector3i center;
        lock (_playerPosLock) center = _currentPlayerChunkPos;
        int dx = pos.X - center.X;
        int dz = pos.Z - center.Z;
        int distSq = dx * dx + dz * dz;

        // Отправляем с ПРИОРИТЕТОМ = ДИСТАНЦИЯ^2.
        // Близкие (маленькое число) встают в начало очереди.
        _chunkGenerator.EnqueueTask(pos, distSq);
    }

    private void ApplyUnloads() { while (_unloadQueue.TryDequeue(out var pos)) UnloadChunk(pos); }

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
            _chunksInProgress.Remove(position);
        } 
    }
    
    private void ProcessGeneratedChunks() 
    { 
        long maxTicks = Stopwatch.Frequency / 1000 * 8; 
        long startTicks = Stopwatch.GetTimestamp(); 
        
        while (_chunkGenerator.TryGetResult(out var result)) 
        { 
            if (result.Voxels == null)
            {
                lock(_chunksLock) _chunksInProgress.Remove(result.Position);
                continue;
            }

            lock(_chunksLock) _chunksInProgress.Remove(result.Position);
            
            Chunk chunkToAdd = null; 
            lock (_chunksLock) 
            { 
                // Опционально: проверка дистанции перед загрузкой (чтобы не грузить то, от чего уже убежали)
                Vector3i center;
                lock(_playerPosLock) center = _currentPlayerChunkPos;
                int dx = result.Position.X - center.X;
                int dz = result.Position.Z - center.Z;
                int loadLimit = GameSettings.RenderDistance + 3; 

                if (dx*dx + dz*dz <= loadLimit*loadLimit)
                {
                     if (!_chunks.ContainsKey(result.Position)) 
                     { 
                         chunkToAdd = new Chunk(result.Position, this); 
                         chunkToAdd.SetDataFromArray(result.Voxels); 
                         _chunks[result.Position] = chunkToAdd; 
                     }
                }
            } 
            
            System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels); 
            
            if (chunkToAdd != null) 
            { 
                OnChunkLoaded?.Invoke(chunkToAdd); 
                _physicsBuilder.EnqueueTask(chunkToAdd, urgent: true); 
            } 
            
            if (Stopwatch.GetTimestamp() - startTicks > maxTicks) break; 
        } 
    }
    
    // Boilerplate (без изменений)
    private void ProcessPhysicsResults() { long maxTicks = Stopwatch.Frequency / 1000 * 3; long startTicks = Stopwatch.GetTimestamp(); while (_physicsBuilder.TryGetResult(out var result)) { if (!result.IsValid) continue; using (result.Data) { if (result.TargetChunk == null || !result.TargetChunk.IsLoaded) continue; StaticHandle handle = default; if (result.Data.CollidersArray != null && result.Data.Count > 0) { handle = PhysicsWorld.AddStaticChunkBody( (result.TargetChunk.Position * Constants.ChunkSizeWorld).ToSystemNumerics(), result.Data.CollidersArray, result.Data.Count ); } result.TargetChunk.OnPhysicsRebuilt(handle); } if (Stopwatch.GetTimestamp() - startTicks > maxTicks) break; } }
    private void ProcessNewDebris() { while (_objectsCreationQueue.TryDequeue(out var data)) { var vo = new VoxelObject(data.Voxels, data.Material); vo.OnEmpty += QueueForRemoval; var handle = PhysicsWorld.CreateVoxelObjectBody(data.Voxels, data.Material, data.WorldPosition, out var com); var realPos = data.WorldPosition + com; var bodyRef = PhysicsWorld.Simulation.Bodies.GetBodyReference(handle); bodyRef.Pose.Position = realPos; vo.InitializePhysics(handle, com.ToOpenTK()); _voxelObjects.Add(vo); RegisterVoxelObject(handle, vo); } }
    private void ProcessVoxelObjects() { foreach (var vo in _voxelObjects) { if (PhysicsWorld.Simulation.Bodies.BodyExists(vo.BodyHandle)) { var pose = PhysicsWorld.GetPose(vo.BodyHandle); vo.UpdatePose(pose); } } }
    private void ProcessRemovals() { foreach (var obj in _objectsToRemove) { try { _bodyToVoxelObjectMap.Remove(obj.BodyHandle); _voxelObjects.Remove(obj); PhysicsWorld.RemoveBody(obj.BodyHandle); obj.Dispose(); } catch { } } _objectsToRemove.Clear(); }
    public OpenTK.Mathematics.Vector3 GetPlayerPosition() => PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle).Pose.Position.ToOpenTK();
    public void Dispose() { if (_isDisposed) return; _isDisposed = true; _schedulerCts.Cancel(); if (_schedulerThread.IsAlive) _schedulerThread.Join(); _chunkGenerator.Dispose(); _physicsBuilder.Dispose(); _integritySystem.Dispose(); lock (_chunksLock) { foreach (var chunk in _chunks.Values) chunk.Dispose(); _chunks.Clear(); } foreach(var vo in _voxelObjects) vo.Dispose(); _voxelObjects.Clear(); }
    public List<Chunk> GetChunksSnapshot() { lock (_chunksLock) return new List<Chunk>(_chunks.Values); }
    public float GetViewRangeInMeters() => GameSettings.RenderDistance * Constants.ChunkSizeWorld;
    public void RebuildPhysics(Chunk chunk) { if (chunk == null || !chunk.IsLoaded) return; _physicsBuilder.EnqueueTask(chunk, urgent: true); }
    public void CreateDetachedObject(List<Vector3i> globalCluster) { foreach (var pos in globalCluster) RemoveVoxelGlobal(pos); OpenTK.Mathematics.Vector3 minIndex = new OpenTK.Mathematics.Vector3(float.MaxValue); foreach (var v in globalCluster) minIndex = OpenTK.Mathematics.Vector3.ComponentMin(minIndex, new OpenTK.Mathematics.Vector3(v.X, v.Y, v.Z)); List<Vector3i> localVoxels = new List<Vector3i>(); Vector3i minIdxInt = new Vector3i((int)minIndex.X, (int)minIndex.Y, (int)minIndex.Z); foreach (var v in globalCluster) localVoxels.Add(v - minIdxInt); System.Numerics.Vector3 worldPos = (minIndex * Constants.VoxelSize).ToSystemNumerics(); _objectsCreationQueue.Enqueue(new VoxelObjectCreationData { Voxels = localVoxels, Material = MaterialType.Stone, WorldPosition = worldPos }); }
    public void QueueForRemoval(VoxelObject obj) => _objectsToRemove.Add(obj);
    public void DestroyVoxelAt(CollidableReference collidable, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal) { var pointInside = worldHitLocation - worldHitNormal * (Constants.VoxelSize * 0.5f); if (collidable.Mobility == CollidableMobility.Static) { Vector3i globalVoxelIndex = new Vector3i((int)Math.Floor(pointInside.X / Constants.VoxelSize), (int)Math.Floor(pointInside.Y / Constants.VoxelSize), (int)Math.Floor(pointInside.Z / Constants.VoxelSize)); if (RemoveVoxelGlobal(globalVoxelIndex)) { NotifyVoxelFastDestroyed(globalVoxelIndex); _integritySystem.QueueCheck(globalVoxelIndex); } } else if (collidable.Mobility == CollidableMobility.Dynamic && _bodyToVoxelObjectMap.TryGetValue(collidable.BodyHandle, out var voxelObj)) { Matrix4 model = Matrix4.CreateTranslation(-voxelObj.LocalCenterOfMass) * Matrix4.CreateFromQuaternion(voxelObj.Rotation) * Matrix4.CreateTranslation(voxelObj.Position); Matrix4 invModel = Matrix4.Invert(model); Vector4 localHitMeters = new Vector4(pointInside.ToOpenTK(), 1.0f) * invModel; Vector3i localVoxelIndex = new Vector3i((int)Math.Floor(localHitMeters.X / Constants.VoxelSize), (int)Math.Floor(localHitMeters.Y / Constants.VoxelSize), (int)Math.Floor(localHitMeters.Z / Constants.VoxelSize)); if (voxelObj.RemoveVoxel(localVoxelIndex)) { if (voxelObj.VoxelCoordinates.Count == 0) return; var clusters = voxelObj.GetConnectedClusters(); if (clusters.Count == 1) { voxelObj.RebuildMeshAndPhysics(PhysicsWorld); } else { clusters.Sort((a, b) => b.Count.CompareTo(a.Count)); var mainCluster = clusters[0]; var mainSet = new HashSet<Vector3i>(mainCluster); voxelObj.VoxelCoordinates.RemoveAll(v => !mainSet.Contains(v)); voxelObj.RebuildMeshAndPhysics(PhysicsWorld); for (int i = 1; i < clusters.Count; i++) SpawnSplitCluster(clusters[i], voxelObj); } } } }
    private void SpawnSplitCluster(List<Vector3i> localClusterVoxels, VoxelObject parentObj) { Vector3i anchorIdx = localClusterVoxels[0]; List<Vector3i> newLocalVoxels = new List<Vector3i>(); foreach(var v in localClusterVoxels) newLocalVoxels.Add(v - anchorIdx); System.Numerics.Vector3 calculatedLocalCoM = System.Numerics.Vector3.Zero; foreach (var v in newLocalVoxels) calculatedLocalCoM += (v.ToSystemNumerics() + new System.Numerics.Vector3(0.5f)) * Constants.VoxelSize; if (newLocalVoxels.Count > 0) calculatedLocalCoM /= newLocalVoxels.Count; System.Numerics.Vector3 anchorPosInParent = (anchorIdx.ToSystemNumerics() + new System.Numerics.Vector3(0.5f)) * Constants.VoxelSize; anchorPosInParent -= parentObj.LocalCenterOfMass.ToSystemNumerics(); System.Numerics.Vector3 anchorWorldPos = parentObj.Position.ToSystemNumerics() + System.Numerics.Vector3.Transform(anchorPosInParent, parentObj.Rotation.ToSystemNumerics()); System.Numerics.Vector3 anchorInNewLocal = new System.Numerics.Vector3(0.5f) * Constants.VoxelSize; System.Numerics.Vector3 offset = anchorInNewLocal - calculatedLocalCoM; System.Numerics.Vector3 rotatedOffset = System.Numerics.Vector3.Transform(offset, parentObj.Rotation.ToSystemNumerics()); System.Numerics.Vector3 finalSpawnPos = anchorWorldPos - rotatedOffset; _objectsCreationQueue.Enqueue(new VoxelObjectCreationData { Voxels = newLocalVoxels, Material = parentObj.Material, WorldPosition = finalSpawnPos }); }
    private bool RemoveVoxelGlobal(Vector3i globalVoxelIndex) { Vector3i chunkPos = GetChunkPosFromVoxelIndex(globalVoxelIndex); if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded) { int res = Constants.ChunkResolution; int lx = globalVoxelIndex.X % res; if(lx < 0) lx += res; int ly = globalVoxelIndex.Y % res; if(ly < 0) ly += res; int lz = globalVoxelIndex.Z % res; if(lz < 0) lz += res; return chunk.RemoveVoxelAndUpdate(new Vector3i(lx, ly, lz)); } return false; }
    private Vector3i GetChunkPosFromVoxelIndex(Vector3i voxelIndex) => new Vector3i((int)Math.Floor((float)voxelIndex.X / Constants.ChunkResolution), (int)Math.Floor((float)voxelIndex.Y / Constants.ChunkResolution), (int)Math.Floor((float)voxelIndex.Z / Constants.ChunkResolution));
    public bool IsVoxelSolidGlobal(Vector3i globalVoxelIndex) { Vector3i chunkPos = GetChunkPosFromVoxelIndex(globalVoxelIndex); if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded) { int res = Constants.ChunkResolution; int lx = globalVoxelIndex.X % res; if (lx < 0) lx += res; int ly = globalVoxelIndex.Y % res; if (ly < 0) ly += res; int lz = globalVoxelIndex.Z % res; if (lz < 0) lz += res; return chunk.IsVoxelSolidAt(new Vector3i(lx, ly, lz)); } return false; }
    public bool IsChunkLoadedAt(Vector3i globalVoxelIndex) { Vector3i chunkPos = GetChunkPosFromVoxelIndex(globalVoxelIndex); lock(_chunksLock) return _chunks.ContainsKey(chunkPos) && _chunks[chunkPos].IsLoaded; }
    public void NotifyVoxelFastDestroyed(Vector3i worldPos) => OnVoxelFastDestroyed?.Invoke(worldPos);
    public void NotifyChunkModified(Chunk chunk) => OnChunkModified?.Invoke(chunk);
    public void RegisterVoxelObject(BodyHandle handle, VoxelObject obj) { lock (_chunksLock) _bodyToVoxelObjectMap[handle] = obj; }
    public void RegisterChunkStatic(StaticHandle handle, Chunk chunk) { lock (_chunksLock) _staticToChunkMap[handle] = chunk; }
    public void UnregisterChunkStatic(StaticHandle handle) { lock (_chunksLock) _staticToChunkMap.Remove(handle); }
    public void SpawnTestVoxel(System.Numerics.Vector3 position, MaterialType material) { var voxels = new List<Vector3i> { new Vector3i(0, 0, 0) }; _objectsCreationQueue.Enqueue(new VoxelObjectCreationData { Voxels = voxels, Material = material, WorldPosition = position }); }
}