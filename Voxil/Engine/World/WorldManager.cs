using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BepuVector3 = System.Numerics.Vector3;

public class WorldManager : IDisposable, IWorldService
{
    public PhysicsWorld PhysicsWorld { get; }

    private readonly PlayerController _playerController;
    private readonly AsyncChunkGenerator _chunkGenerator;
    private readonly AsyncChunkPhysics _physicsBuilder;
    private readonly StructuralIntegritySystem _integritySystem;

    // Хранилище чанков
    private readonly Dictionary<Vector3i, Chunk> _chunks = new();
    private readonly Dictionary<StaticHandle, Chunk> _staticToChunkMap = new();
    private readonly object _chunksLock = new();
    private readonly HashSet<Vector3i> _chunksInProgress = new();

    // Планировщик
    private Thread _schedulerThread;
    private CancellationTokenSource _schedulerCts = new CancellationTokenSource();
    private readonly ConcurrentQueue<Vector3i> _unloadQueue = new();
    private Vector3i _currentPlayerChunkPos = new(int.MaxValue);
    private readonly object _playerPosLock = new();
    private volatile bool _positionChangedDirty = false;

    private bool _isDisposed = false;
    private float _memoryLogTimer = 0f;
    private readonly Stopwatch _mainThreadStopwatch = new Stopwatch();

    public const int WorldHeightChunks = 16;

    // События
    public event Action<Chunk> OnChunkLoaded;
    public event Action<Chunk> OnChunkModified;
    public event Action<Vector3i> OnChunkUnloaded;
    public event Action<Vector3i> OnVoxelFastDestroyed;
    public event Action<Chunk, Vector3i, MaterialType> OnVoxelEdited;

    // Счётчики
    public int LoadedChunkCount => _chunks.Count;
    public int ChunksInProgressCount => _chunksInProgress.Count;
    public int UnloadQueueCount => _unloadQueue.Count;
    public int GeneratorPendingCount => _chunkGenerator.PendingCount;
    public int GeneratorResultsCount => _chunkGenerator.ResultsCount;
    public int PhysicsUrgentCount => _physicsBuilder.UrgentCount;
    public int PhysicsPendingCount => _physicsBuilder.PendingCount;
    public int PhysicsResultsCount => _physicsBuilder.ResultsCount;

    public WorldManager(PhysicsWorld physicsWorld, PlayerController playerController)
    {
        PhysicsWorld = physicsWorld;
        _playerController = playerController;
        _chunkGenerator = new AsyncChunkGenerator(12345, GameSettings.GenerationThreads);
        _physicsBuilder = new AsyncChunkPhysics();
        _integritySystem = new StructuralIntegritySystem();

        if (physicsWorld != null)
        {
            _schedulerThread = new Thread(SchedulerLoop)
            {
                IsBackground = true,
                Name = "WorldScheduler",
                Priority = ThreadPriority.AboveNormal
            };
            _schedulerThread.Start();
        }
    }

    // -------------------------------------------------------------------------
    // Update
    // -------------------------------------------------------------------------

    public void Update(float deltaTime)
    {
        var playerPos = GetPlayerPosition();
        var currentChunkPos = new Vector3i(
            (int)Math.Floor(playerPos.X / Constants.ChunkSizeWorld),
            0,
            (int)Math.Floor(playerPos.Z / Constants.ChunkSizeWorld));

        lock (_playerPosLock)
        {
            if (currentChunkPos != _currentPlayerChunkPos)
            {
                _currentPlayerChunkPos = currentChunkPos;
                _positionChangedDirty = true;
            }
        }

        _mainThreadStopwatch.Restart();

        double targetFrameTimeMs = 1000.0 / GameSettings.TargetFPSForBudgeting;
        double totalBudget = targetFrameTimeMs * GameSettings.WorldUpdateBudgetPercentage;
        double physicsBudget = totalBudget * 0.6;
        double genBudget = totalBudget * 0.4;

        ApplyUnloads();
        ProcessPhysicsResults(physicsBudget);
        ProcessGeneratedChunks(genBudget);

        // Делегируем обновление динамических объектов
        ServiceLocator.Get<IVoxelObjectService>().Update(_mainThreadStopwatch);

        _memoryLogTimer += deltaTime;
        if (_memoryLogTimer >= 5.0f) _memoryLogTimer = 0f;
    }

    // -------------------------------------------------------------------------
    // Планировщик
    // -------------------------------------------------------------------------

    private void SchedulerLoop()
    {
        Vector3i lastScheduledCenter = new(int.MaxValue);
        while (!_schedulerCts.IsCancellationRequested)
        {
            Vector3i center;
            lock (_playerPosLock) center = _currentPlayerChunkPos;

            if (center.X == int.MaxValue)
            {
                Thread.Sleep(100);
                continue;
            }

            bool positionChanged = false;
            if (center != lastScheduledCenter)
            {
                lastScheduledCenter = center;
                ScheduleUnloads();
                positionChanged = true;
            }

            if (positionChanged)
                lock (_playerPosLock) _positionChangedDirty = false;

            int viewDist = GameSettings.RenderDistance;
            int scheduledCount = 0;

            for (int r = 0; r <= viewDist; r++)
            {
                if (_chunkGenerator.PendingCount > 150) break;
                if (_positionChangedDirty) break;

                bool addedInRing = false;
                ProcessRing(center, r, WorldHeightChunks, viewDist, ref addedInRing);
                if (addedInRing) scheduledCount++;
            }

            Thread.Sleep(scheduledCount == 0 ? 30 : 1);
        }
    }

    private void ScheduleUnloads()
    {
        Vector3i center;
        lock (_playerPosLock) center = _currentPlayerChunkPos;
        if (center.X == int.MaxValue) return;

        int viewDist = GameSettings.RenderDistance;
        long safeUnloadDistSq = (long)(viewDist + 4) * (viewDist + 4);

        lock (_chunksLock)
        {
            foreach (var pos in _chunks.Keys)
            {
                long dx = pos.X - center.X;
                long dz = pos.Z - center.Z;
                if (dx * dx + dz * dz > safeUnloadDistSq)
                    _unloadQueue.Enqueue(pos);
            }
        }
    }

    private void ProcessRing(Vector3i center, int radius, int height, int maxViewDist, ref bool addedAny)
    {
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
        long dx = pos.X - center.X;
        long dz = pos.Z - center.Z;
        long distSq = dx * dx + dz * dz;
        if (distSq > maxDistSq) return false;

        lock (_chunksLock)
        {
            if (_chunks.ContainsKey(pos)) return false;
            if (_chunksInProgress.Contains(pos)) return false;
            _chunksInProgress.Add(pos);
        }

        int priority = distSq > int.MaxValue ? int.MaxValue : (int)distSq;
        _chunkGenerator.EnqueueTask(pos, priority);
        return true;
    }

    private void ApplyUnloads()
    {
        while (_unloadQueue.TryDequeue(out var pos)) UnloadChunk(pos);
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
            _chunksInProgress.Remove(position);
        }
    }

    private void ProcessGeneratedChunks(double budgetMs)
    {
        while (_chunkGenerator.TryGetResult(out var result))
        {
            lock (_chunksLock) _chunksInProgress.Remove(result.Position);
            if (result.Voxels == null) continue;

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
                _physicsBuilder.EnqueueTask(chunkToAdd, urgent: true);

            if (_mainThreadStopwatch.Elapsed.TotalMilliseconds >= budgetMs) break;
        }
    }

    private void ProcessPhysicsResults(double budgetMs)
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
                    result.Data.Count);
            }
            result.TargetChunk.OnPhysicsRebuilt(handle);
            OnChunkLoaded?.Invoke(result.TargetChunk);

            if (_mainThreadStopwatch.Elapsed.TotalMilliseconds >= budgetMs) break;
        }
    }

    // -------------------------------------------------------------------------
    // IWorldService — чанки
    // -------------------------------------------------------------------------

    public Chunk GetChunk(Vector3i position)
    {
        lock (_chunksLock)
        {
            _chunks.TryGetValue(position, out var chunk);
            return chunk;
        }
    }

    public List<Chunk> GetChunksSnapshot()
    {
        lock (_chunksLock) return new List<Chunk>(_chunks.Values);
    }

    public Dictionary<Vector3i, Chunk> GetAllChunks() => _chunks;

    public bool IsChunkLoadedAt(Vector3i globalVoxelIndex)
    {
        Vector3i chunkPos = GetChunkPosFromVoxelIndex(globalVoxelIndex);
        lock (_chunksLock) return _chunks.ContainsKey(chunkPos) && _chunks[chunkPos].IsLoaded;
    }

    public float GetViewRangeInMeters() => GameSettings.RenderDistance * Constants.ChunkSizeWorld;

    // -------------------------------------------------------------------------
    // IWorldService — делегаты к другим сервисам
    // -------------------------------------------------------------------------

    public List<VoxelObject> GetAllVoxelObjects()
        => ServiceLocator.Get<IVoxelObjectService>().GetAllVoxelObjects();

    public void SpawnDynamicObject(VoxelObject obj, System.Numerics.Vector3 position, System.Numerics.Vector3 velocity)
        => ServiceLocator.Get<IVoxelObjectService>().SpawnDynamicObject(obj, position, velocity);

    public void SpawnComplexObject(System.Numerics.Vector3 position, List<Vector3i> localVoxels, MaterialType material)
        => ServiceLocator.Get<IVoxelObjectService>().SpawnComplexObject(position, localVoxels, material);

    public void SpawnComplexObject(System.Numerics.Vector3 position, List<Vector3i> localVoxels, MaterialType material, Dictionary<Vector3i, uint> perVoxelMaterials)
        => ServiceLocator.Get<IVoxelObjectService>().SpawnComplexObject(position, localVoxels, material, perVoxelMaterials);

    public void DestroyVoxelObject(VoxelObject obj)
        => ServiceLocator.Get<IVoxelObjectService>().DestroyVoxelObject(obj);

    public void CreateDetachedObject(List<Vector3i> globalCluster)
        => ServiceLocator.Get<IVoxelObjectService>().CreateDetachedObject(globalCluster);

    public void ProcessDynamicObjectSplits(VoxelObject vo)
        => ServiceLocator.Get<IVoxelObjectService>().ProcessDynamicObjectSplits(vo);

    public MaterialType GetMaterialGlobal(Vector3i globalPos)
        => ServiceLocator.Get<IVoxelEditService>().GetMaterialGlobal(globalPos);

    public bool IsVoxelSolidGlobal(Vector3i globalPos)
        => ServiceLocator.Get<IVoxelEditService>().IsVoxelSolidGlobal(globalPos);

    public bool RemoveVoxelGlobal(Vector3i globalPos)
        => ServiceLocator.Get<IVoxelEditService>().RemoveVoxelGlobal(globalPos);

    public void DestroyVoxelAt(CollidableReference collidable, System.Numerics.Vector3 hitPoint, System.Numerics.Vector3 hitNormal)
        => ServiceLocator.Get<IVoxelEditService>().DestroyVoxelAt(collidable, hitPoint, hitNormal);

    public void ApplyDamageToStatic(Vector3i globalPos, float damage, out bool destroyed)
        => ServiceLocator.Get<IVoxelEditService>().ApplyDamageToStatic(globalPos, damage, out destroyed);

    public void MarkChunkDirty(Vector3i globalVoxelIndex)
        => ServiceLocator.Get<IVoxelEditService>().MarkChunkDirty(globalVoxelIndex);

    public void UpdateDirtyChunks()
        => ServiceLocator.Get<IVoxelEditService>().UpdateDirtyChunks();

    public void GetStaticVoxelHealthInfo(Vector3i globalPos, out float currentHP, out float maxHP)
        => ServiceLocator.Get<IVoxelEditService>().GetStaticVoxelHealthInfo(globalPos, out currentHP, out maxHP);

    public void NotifyVoxelEdited(Chunk chunk, Vector3i pos, MaterialType mat)
        => OnVoxelEdited?.Invoke(chunk, pos, mat);

    public void NotifyVoxelFastDestroyed(Vector3i worldPos)
        => OnVoxelFastDestroyed?.Invoke(worldPos);

    public void NotifyChunkModified(Chunk chunk)
        => OnChunkModified?.Invoke(chunk);

    // -------------------------------------------------------------------------
    // IWorldService — физика чанков
    // -------------------------------------------------------------------------

    public void RebuildPhysics(Chunk chunk)
    {
        if (chunk == null || !chunk.IsLoaded) return;
        _physicsBuilder.EnqueueTask(chunk, urgent: true);
    }

    // -------------------------------------------------------------------------
    // IWorldService — утилиты
    // -------------------------------------------------------------------------

    public OpenTK.Mathematics.Vector3 GetPlayerPosition()
    {
        if (_playerController == null) return OpenTK.Mathematics.Vector3.Zero;
        if (PhysicsWorld.Simulation.Bodies.BodyExists(_playerController.BodyHandle))
            return PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle).Pose.Position.ToOpenTK();

        Console.WriteLine("[CRITICAL] Player body does not exist!");
        return OpenTK.Mathematics.Vector3.Zero;
    }

    public System.Numerics.Vector3 GetPlayerVelocity()
    {
        if (_playerController != null && PhysicsWorld.Simulation.Bodies.BodyExists(_playerController.BodyHandle))
            return PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle).Velocity.Linear;
        return System.Numerics.Vector3.Zero;
    }

    public void SetGenerationThreadCount(int count) => _chunkGenerator.SetThreadCount(count);

    public void ReloadWorld()
    {
        ClearAndStopWorld();
        Console.WriteLine("[World] Restarting scheduler...");
        lock (_playerPosLock)
        {
            _currentPlayerChunkPos = new(int.MaxValue);
            _positionChangedDirty = true;
        }
        _schedulerCts.Dispose();
        _schedulerCts = new CancellationTokenSource();
        _schedulerThread = new Thread(SchedulerLoop)
        {
            IsBackground = true,
            Name = "WorldScheduler",
            Priority = ThreadPriority.AboveNormal
        };
        _schedulerThread.Start();
        Console.WriteLine("[World] Scheduler restarted.");
    }

    public void ClearAndStopWorld()
    {
        Console.WriteLine("[World] Stopping and clearing world state...");
        _schedulerCts.Cancel();
        if (_schedulerThread != null && _schedulerThread.IsAlive)
            _schedulerThread.Join(1000);

        _schedulerCts.Dispose();
        _chunkGenerator.ClearQueue();
        _physicsBuilder.Clear();

        while (_chunkGenerator.TryGetResult(out var result))
            if (result.Voxels != null)
                System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels);

        while (_physicsBuilder.TryGetResult(out _)) { }
        while (_unloadQueue.TryDequeue(out _)) { }

        lock (_chunksLock)
        {
            var keysToUnload = new List<Vector3i>(_chunks.Keys);
            foreach (var pos in keysToUnload) UnloadChunk(pos);
        }
        lock (_chunksLock)
        {
            _chunks.Clear();
            _chunksInProgress.Clear();
            _staticToChunkMap.Clear();
        }
        Console.WriteLine("[World] World cleared.");
    }

    // -------------------------------------------------------------------------
    // Регистрация (используется из сервисов)
    // -------------------------------------------------------------------------

    public void RegisterChunkStatic(StaticHandle handle, Chunk chunk)
    {
        lock (_chunksLock) _staticToChunkMap[handle] = chunk;
    }

    public void UnregisterChunkStatic(StaticHandle handle)
    {
        lock (_chunksLock) _staticToChunkMap.Remove(handle);
    }

    // -------------------------------------------------------------------------
    // Тесты
    // -------------------------------------------------------------------------

    public void TestBreakVoxel(VoxelObject vo, Vector3i localPos)
        => ServiceLocator.Get<IVoxelObjectService>().TestBreakVoxel(vo, localPos);

    // -------------------------------------------------------------------------
    // Вспомогательные
    // -------------------------------------------------------------------------

    public Vector3i GetChunkPosFromVoxelIndex(Vector3i voxelIndex) => new Vector3i(
        (int)Math.Floor((float)voxelIndex.X / Constants.ChunkResolution),
        (int)Math.Floor((float)voxelIndex.Y / Constants.ChunkResolution),
        (int)Math.Floor((float)voxelIndex.Z / Constants.ChunkResolution));

    // -------------------------------------------------------------------------
    // Dispose
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (_isDisposed) return;
        _schedulerCts.Dispose();
        _isDisposed = true;
        _schedulerCts.Cancel();
        if (_schedulerThread != null && _schedulerThread.IsAlive)
            _schedulerThread.Join();

        _chunkGenerator.Dispose();
        _physicsBuilder.Dispose();
        _integritySystem.Dispose();

        lock (_chunksLock)
        {
            foreach (var chunk in _chunks.Values) chunk.Dispose();
            _chunks.Clear();
        }
    }
}