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

    // Хранилище
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
    private readonly HashSet<Vector3i> _chunksInProgress = new();

    // Инициализируем int.MaxValue, чтобы форсировать обновление при первом кадре
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

    // ТАЙМЕРЫ
    // Мы убрали жесткие лимиты на передачу данных, но оставим таймер для общего контроля, если понадобится
    private readonly Stopwatch _mainThreadStopwatch = new Stopwatch();

    private struct VoxelObjectCreationData { public List<Vector3i> Voxels; public MaterialType Material; public System.Numerics.Vector3 WorldPosition; }
    
    // СЧЕТЧИКИ
    public int LoadedChunkCount => _chunks.Count;
    public int ChunksInProgressCount => _chunksInProgress.Count;
    public int UnloadQueueCount => _unloadQueue.Count;
    public int GeneratorPendingCount => _chunkGenerator.PendingCount;
    public int GeneratorResultsCount => _chunkGenerator.ResultsCount;
    public int PhysicsUrgentCount => _physicsBuilder.UrgentCount;
    public int PhysicsPendingCount => _physicsBuilder.PendingCount;
    public int PhysicsResultsCount => _physicsBuilder.ResultsCount;

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
        ClearAndStopWorld();

        // Перезапускаем все с нуля
        Console.WriteLine("[World] Restarting scheduler...");
        lock (_playerPosLock)
        {
            _currentPlayerChunkPos = new(int.MaxValue);
            _positionChangedDirty = true;
        }
    
        _schedulerCts = new CancellationTokenSource();
        _schedulerThread = new Thread(SchedulerLoop)
        {
            IsBackground = true,
            Name = "WorldScheduler",
            Priority = ThreadPriority.AboveNormal
        };
        _schedulerThread.Start();
    }
    
    // ДОБАВЬ ЭТОТ НОВЫЙ МЕТОД В WorldManager.cs
    public void ClearAndStopWorld()
    {
        Console.WriteLine("[World] Stopping and clearing world state...");

        // 1. Останавливаем всю фоновую работу
        _schedulerCts.Cancel();
        if (_schedulerThread != null && _schedulerThread.IsAlive)
        {
            _schedulerThread.Join();
        }

        // 2. Вычищаем все асинхронные очереди
        _chunkGenerator.ClearQueue();
        _physicsBuilder.Clear();
        while (_chunkGenerator.TryGetResult(out var result)) 
        {
            if(result.Voxels != null) System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels);
        }
        while (_physicsBuilder.TryGetResult(out _)) { }
        while (_unloadQueue.TryDequeue(out _)) { }

        // 3. ГРАМОТНО выгружаем чанки, чтобы рендерер получил уведомления
        lock (_chunksLock)
        {
            if (_chunks.Count > 0)
            {
                var keysToUnload = new List<Vector3i>(_chunks.Keys);
                foreach (var pos in keysToUnload)
                {
                    UnloadChunk(pos);
                }
            }
        }
    
        // 4. Очищаем все CPU-данные
        lock (_chunksLock)
        {
            _chunks.Clear();
            _chunksInProgress.Clear();
            _staticToChunkMap.Clear();
        }
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
            }
        }

        ApplyUnloads();
        ProcessGeneratedChunks(); 
        ProcessPhysicsResults(); 
        
        ProcessNewDebris();
        ProcessVoxelObjects();
        ProcessRemovals();

        _memoryLogTimer += deltaTime;
        if (_memoryLogTimer >= 5.0f) { _memoryLogTimer = 0f; }
    }
    
    private void SchedulerLoop()
    {
        Vector3i lastScheduledCenter = new(int.MaxValue);

        while (!_schedulerCts.IsCancellationRequested)
        {
            Vector3i center;
            lock(_playerPosLock) center = _currentPlayerChunkPos;
            
            // --- ЗАЩИТА ОТ НЕКОРРЕКТНОЙ ПОЗИЦИИ ---
            // Если мир перезагружается или еще не стартовал, ждем.
            if (center.X == int.MaxValue)
            {
                Thread.Sleep(10);
                continue;
            }
            // -------------------------------------

            if (center != lastScheduledCenter)
            {
                lastScheduledCenter = center;
                ScheduleUnloads();
            }

            int viewDist = GameSettings.RenderDistance;
            bool dirty = false;
            int scheduledCount = 0;

            for (int r = 0; r <= viewDist; r++)
            {
                if (_positionChangedDirty) { dirty = true; break; }

                // Троттлинг очереди генератора
                while (_chunkGenerator.PendingCount > 150) 
                {
                    if (_positionChangedDirty || _schedulerCts.IsCancellationRequested) { dirty = true; break; }
                    Thread.Sleep(2);
                }
                if (dirty) break;

                bool added = false;
                ProcessRing(center, r, WorldHeightChunks, viewDist, ref added);
                if (added) scheduledCount++;
            }
            
            if (_positionChangedDirty)
            {
                lock(_playerPosLock) _positionChangedDirty = false;
            }
            else
            {
                // Если работы нет, спим
                if (scheduledCount == 0) Thread.Sleep(30);
                else Thread.Sleep(1);
            }
        }
    }

    private void ScheduleUnloads()
    {
        Vector3i center;
        lock (_playerPosLock) center = _currentPlayerChunkPos;
        if (center.X == int.MaxValue) return; // Защита

        int viewDist = GameSettings.RenderDistance;
        long safeUnloadDistSq = (long)(viewDist + 4) * (viewDist + 4);

        lock(_chunksLock)
        {
            foreach(var pos in _chunks.Keys)
            {
                long dx = pos.X - center.X;
                long dz = pos.Z - center.Z;
                if (dx * dx + dz * dz > safeUnloadDistSq) 
                {
                    _unloadQueue.Enqueue(pos);
                }
            }
        }
    }

    private void ProcessRing(Vector3i center, int radius, int height, int maxViewDist, ref bool addedAny)
    {
        // Квадрат максимальной дистанции (КРУГ)
        long maxDistSq = (long)maxViewDist * maxViewDist;

        if (radius == 0)
        {
            for (int y = 0; y < height; y++) 
                if (TrySchedule(new Vector3i(center.X, y, center.Z), maxDistSq, center)) addedAny = true;
            return;
        }
        
        for (int i = -radius; i <= radius; i++)
        {
            for (int y = 0; y < height; y++)
            {
                if (TrySchedule(new Vector3i(center.X + i, y, center.Z + radius), maxDistSq, center)) addedAny = true;
                if (TrySchedule(new Vector3i(center.X + i, y, center.Z - radius), maxDistSq, center)) addedAny = true;
                if (i > -radius && i < radius)
                {
                    if (TrySchedule(new Vector3i(center.X + radius, y, center.Z + i), maxDistSq, center)) addedAny = true;
                    if (TrySchedule(new Vector3i(center.X - radius, y, center.Z + i), maxDistSq, center)) addedAny = true;
                }
            }
        }
    }
    
    private bool TrySchedule(Vector3i pos, long maxDistSq, Vector3i center)
    {
        // 1. Проверка дистанции (Круг)
        long dx = pos.X - center.X;
        long dz = pos.Z - center.Z;
        long distSq = dx * dx + dz * dz;

        // Если это угол квадрата, выходящий за круг - не планируем
        if (distSq > maxDistSq) return false;

        lock (_chunksLock)
        {
            if (_chunks.ContainsKey(pos)) return false;
            if (_chunksInProgress.Contains(pos)) return false;
            _chunksInProgress.Add(pos);
        }

        // Приоритет = дистанция
        int priority = (distSq > int.MaxValue) ? int.MaxValue : (int)distSq;
        _chunkGenerator.EnqueueTask(pos, priority);
        return true;
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
        while (_chunkGenerator.TryGetResult(out var result)) 
        { 
            lock(_chunksLock) _chunksInProgress.Remove(result.Position);

            if (result.Voxels == null) continue;

            Chunk chunkToAdd = null; 
            lock (_chunksLock) 
            { 
                Vector3i center;
                lock(_playerPosLock) center = _currentPlayerChunkPos;
                
                // Защита: Если позиция игрока сброшена (релоад), не добавляем старые чанки
                if (center.X != int.MaxValue)
                {
                    long dx = result.Position.X - center.X;
                    long dz = result.Position.Z - center.Z;
                    int loadLimit = GameSettings.RenderDistance + 3; 

                    if (dx*dx + dz*dz <= (long)loadLimit*loadLimit)
                    {
                         if (!_chunks.ContainsKey(result.Position)) 
                         { 
                             chunkToAdd = new Chunk(result.Position, this); 
                             chunkToAdd.SetDataFromArray(result.Voxels); 
                             _chunks[result.Position] = chunkToAdd; 
                         }
                    }
                }
            } 
            
            System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels); 
            
            if (chunkToAdd != null) 
            { 
                OnChunkLoaded?.Invoke(chunkToAdd); 
                _physicsBuilder.EnqueueTask(chunkToAdd, urgent: true); 
            } 
        } 
    }
    
    private void ProcessPhysicsResults() 
    { 
        while (_physicsBuilder.TryGetResult(out var result)) 
        { 
            if (!result.IsValid || result.TargetChunk == null || !result.TargetChunk.IsLoaded) continue;
            
            StaticHandle handle = default;
            
            if (result.Data.Count > 0 && result.Data.CollidersArray != null)
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

    private void ProcessNewDebris() { while (_objectsCreationQueue.TryDequeue(out var data)) { var vo = new VoxelObject(data.Voxels, data.Material); vo.OnEmpty += QueueForRemoval; var handle = PhysicsWorld.CreateVoxelObjectBody(data.Voxels, data.Material, data.WorldPosition, out var com); var realPos = data.WorldPosition + com; var bodyRef = PhysicsWorld.Simulation.Bodies.GetBodyReference(handle); bodyRef.Pose.Position = realPos; vo.InitializePhysics(handle, com.ToOpenTK()); _voxelObjects.Add(vo); RegisterVoxelObject(handle, vo); } }
    private void ProcessVoxelObjects() { foreach (var vo in _voxelObjects) { if (PhysicsWorld.Simulation.Bodies.BodyExists(vo.BodyHandle)) { var pose = PhysicsWorld.GetPose(vo.BodyHandle); vo.UpdatePose(pose); } } }
    private void ProcessRemovals() { foreach (var obj in _objectsToRemove) { try { _bodyToVoxelObjectMap.Remove(obj.BodyHandle); _voxelObjects.Remove(obj); PhysicsWorld.RemoveBody(obj.BodyHandle); obj.Dispose(); } catch { } } _objectsToRemove.Clear(); }
    public OpenTK.Mathematics.Vector3 GetPlayerPosition() => PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle).Pose.Position.ToOpenTK();
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // Корректная остановка потока при выходе из игры
        _schedulerCts.Cancel();
        if (_schedulerThread != null && _schedulerThread.IsAlive)
        {
            _schedulerThread.Join();
        }
    
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