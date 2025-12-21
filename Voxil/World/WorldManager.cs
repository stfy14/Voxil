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
using System.Buffers;
using BepuVector3 = System.Numerics.Vector3;

#region Task Structures
public class ChunkGenerationTask { public Vector3i Position; public int Priority; }
public class ChunkGenerationResult 
{ 
    public Vector3i Position; 
    public MaterialType[] Voxels; // СТАЛО: Массив
}
public class PhysicsBuildTask { public Chunk ChunkToProcess; }
public class PhysicsBuildResult { public Chunk TargetChunk; public List<VoxelCollider> Colliders; }
public class DetachmentCheckTask { public Chunk Chunk; public Vector3i RemovedVoxelLocalPos; }
#endregion

public class WorldManager : IDisposable
{
    public PhysicsWorld PhysicsWorld { get; }
    private readonly PlayerController _playerController;
    private readonly IWorldGenerator _generator;

    // Хранилище чанков
    private readonly Dictionary<Vector3i, Chunk> _chunks = new();
    private readonly Dictionary<BodyHandle, VoxelObject> _bodyToVoxelObjectMap = new();
    private readonly Dictionary<StaticHandle, Chunk> _staticToChunkMap = new();

    private readonly List<VoxelObject> _voxelObjects = new();
    private readonly ConcurrentQueue<VoxelObject> _voxelObjectsToAdd = new();
    private readonly List<VoxelObject> _objectsToRemove = new();

    // Потоки
    private List<Thread> _generationThreads = new List<Thread>();
    private CancellationTokenSource _genCts = new CancellationTokenSource();
    
    private readonly Thread _detachmentThread;
    private readonly Thread _physicsBuilderThread;
    
    private volatile bool _isDisposed = false;

    // Очереди
    private readonly BlockingCollection<ChunkGenerationTask> _generationQueue = new(new ConcurrentQueue<ChunkGenerationTask>());
    private readonly ConcurrentQueue<ChunkGenerationResult> _generatedChunksQueue = new();
    
    private readonly BlockingCollection<PhysicsBuildTask> _physicsBuildQueue = new(new ConcurrentQueue<PhysicsBuildTask>());
    private readonly ConcurrentQueue<PhysicsBuildResult> _physicsResultQueue = new ConcurrentQueue<PhysicsBuildResult>();
    
    private readonly BlockingCollection<DetachmentCheckTask> _detachmentQueue = new(new ConcurrentQueue<DetachmentCheckTask>());
    private readonly ConcurrentQueue<PhysicsBuildTask> _urgentPhysicsQueue = new();

    // Синхронизация
    private readonly object _chunksLock = new();
    private readonly HashSet<Vector3i> _chunksInProgress = new();
    private readonly HashSet<Vector3i> _activeChunkPositions = new();
    
    private Vector3i _lastPlayerChunkPosition = new(int.MaxValue);

    // --- АСИНХРОННОСТЬ ---
    private volatile bool _isChunkUpdateRunning = false;
    private readonly ConcurrentQueue<List<ChunkGenerationTask>> _incomingChunksToLoad = new();
    private readonly ConcurrentQueue<List<Vector3i>> _incomingChunksToUnload = new();

    // --- СОБЫТИЯ ---
    public event Action<Chunk> OnChunkLoaded;     
    public event Action<Chunk> OnChunkModified;   
    public event Action<Vector3i> OnChunkUnloaded; 
    public event Action<Vector3i> OnVoxelFastDestroyed;

    // --- КОНСТАНТЫ ---
    public const int WorldHeightChunks = 16; 

    private float _memoryLogTimer = 0f;

    public Dictionary<Vector3i, Chunk> GetAllChunks() => _chunks;
    public List<VoxelObject> GetAllVoxelObjects() => _voxelObjects;

    public WorldManager(PhysicsWorld physicsWorld, PlayerController playerController)
    {
        PhysicsWorld = physicsWorld;
        _playerController = playerController;
        _generator = new PerlinGenerator(12345);

        SetGenerationThreadCount(GameSettings.GenerationThreads);

        _detachmentThread = new Thread(DetachmentThreadLoop) 
            { IsBackground = true, Name = "DetachmentCheck", Priority = ThreadPriority.Lowest };
        
        _physicsBuilderThread = new Thread(PhysicsBuilderThreadLoop) 
            { IsBackground = true, Name = "PhysicsBuilder", Priority = ThreadPriority.BelowNormal };

        _detachmentThread.Start();
        _physicsBuilderThread.Start();
    }

    public void SetGenerationThreadCount(int count)
    {
        if (_generationThreads.Count == count) return;

        _genCts.Cancel();
        foreach (var t in _generationThreads) if (t.IsAlive) t.Join(10); 
        _generationThreads.Clear();
        _genCts = new CancellationTokenSource();

        for (int i = 0; i < count; i++)
        {
            var t = new Thread(() => GenerationThreadLoop(_genCts.Token))
            {
                IsBackground = true, Priority = ThreadPriority.Lowest, Name = $"GenThread_{i}"
            };
            t.Start();
            _generationThreads.Add(t);
        }
        Console.WriteLine($"[World] Generation threads set to: {count}");
    }

    #region Thread Loops

    private void GenerationThreadLoop(CancellationToken token)
    {
        var stopwatch = new Stopwatch();
        while (!_isDisposed && !token.IsCancellationRequested)
        {
            try
            {
                if (_generationQueue.TryTake(out var task, 100, token))
                {
                    if (PerformanceMonitor.IsEnabled) stopwatch.Restart();

                    // --- ОПТИМИЗАЦИЯ: Rent вместо new ---
                    var voxels = ArrayPool<MaterialType>.Shared.Rent(Chunk.Volume);
                    
                    // Генератор сам делает Array.Fill внутри, так что чистить не обязательно, 
                    // но PerlinGenerator должен гарантированно перезаписывать данные.
                    // PerlinGenerator в вашем коде делает Array.Fill, так что всё ок.
                    
                    _generator.GenerateChunk(task.Position, voxels);

                    if (PerformanceMonitor.IsEnabled) { /* ... */ }

                    _generatedChunksQueue.Enqueue(new ChunkGenerationResult 
                        { Position = task.Position, Voxels = voxels });
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[GenerationThread] Error: {ex.Message}"); }
        }
    }

    public void RebuildPhysics(Chunk chunk)
    {
        if (chunk == null || !chunk.IsLoaded) return;
        _urgentPhysicsQueue.Enqueue(new PhysicsBuildTask { ChunkToProcess = chunk });
    }

    private void PhysicsBuilderThreadLoop()
    {
        while (!_isDisposed)
        {
            try
            {
                PhysicsBuildTask task = null;
                bool gotTask = false;

                if (_urgentPhysicsQueue.TryDequeue(out task)) gotTask = true;
                else if (_physicsBuildQueue.TryTake(out task, 10)) gotTask = true;

                if (gotTask && task != null)
                {
                    if (task.ChunkToProcess == null || !task.ChunkToProcess.IsLoaded) continue;

                    // --- ОПТИМИЗАЦИЯ: Rent ---
                    var rawVoxels = ArrayPool<MaterialType>.Shared.Rent(Chunk.Volume);
                    
                    var src = task.ChunkToProcess.GetVoxelsUnsafe();

                    var rwLock = task.ChunkToProcess.GetLock();
                    rwLock.EnterReadLock();
                    try 
                    { 
                        // Копируем byte[] -> MaterialType[]
                        // Придется делать цикл, т.к. типы разные, Array.Copy не сработает напрямую байт в енум
                        for(int i=0; i < Chunk.Volume; i++)
                        {
                            rawVoxels[i] = (MaterialType)src[i];
                        }
                    }
                    finally { rwLock.ExitReadLock(); }

                    var colliders = VoxelPhysicsBuilder.GenerateColliders(rawVoxels, task.ChunkToProcess.Position);

                    // --- ВОЗВРАТ ---
                    ArrayPool<MaterialType>.Shared.Return(rawVoxels);

                    _physicsResultQueue.Enqueue(new PhysicsBuildResult
                    {
                        TargetChunk = task.ChunkToProcess,
                        Colliders = colliders
                    });
                }
            }
            catch (Exception ex) { Console.WriteLine($"[PhysicsBuilderThread] Error: {ex.Message}"); }
        }
    }

    private void DetachmentThreadLoop()
    {
        while (!_isDisposed)
        {
            if (_detachmentQueue.TryTake(out var task, 50)) { /* Логика отваливающихся кусков */ }
        }
    }

    #endregion

    #region Main Update

    public void Update(float deltaTime)
    {
        UpdateVisibleChunks();
        ProcessGeneratedChunks();
        ProcessPhysicsResults();
        ProcessVoxelObjects();
        ProcessRemovals();

        _memoryLogTimer += deltaTime;
        if (_memoryLogTimer >= 5.0f)
        {
            Console.WriteLine($"[World] Loaded Chunks: {_chunks.Count}");
            _memoryLogTimer = 0f;
        }
    }

    private void UpdateVisibleChunks()
    {
        ApplyChunkUpdates();
        if (_isChunkUpdateRunning) return;

        var playerPos = PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle).Pose.Position;
        var pX = (int)Math.Floor(playerPos.X / Chunk.ChunkSize);
        var pZ = (int)Math.Floor(playerPos.Z / Chunk.ChunkSize);
        var currentCenter = new Vector3i(pX, 0, pZ);

        if (currentCenter == _lastPlayerChunkPosition) return;
        _lastPlayerChunkPosition = currentCenter;

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
        var toLoad = new List<ChunkGenerationTask>();
        var toUnload = new List<Vector3i>();
        var requiredSet = new HashSet<Vector3i>();

        int viewDistSq = viewDist * viewDist;
        
        for (int x = -viewDist; x <= viewDist; x++)
        {
            for (int z = -viewDist; z <= viewDist; z++)
            {
                if (x*x + z*z > viewDistSq) continue;
                for (int y = 0; y < height; y++) requiredSet.Add(new Vector3i(center.X + x, y, center.Z + z));
            }
        }

        foreach (var pos in activeSnapshot) if (!requiredSet.Contains(pos)) toUnload.Add(pos);

        var loadPositions = new List<Vector3i>();
        foreach (var pos in requiredSet) if (!activeSnapshot.Contains(pos)) loadPositions.Add(pos);

        loadPositions.Sort((a, b) => 
        {
            int dxA = a.X - center.X; int dzA = a.Z - center.Z;
            int distA = dxA*dxA + dzA*dzA;
            int dxB = b.X - center.X; int dzB = b.Z - center.Z;
            int distB = dxB*dxB + dzB*dzB;
            int pA = distA * 1000 + Math.Abs(a.Y);
            int pB = distB * 1000 + Math.Abs(b.Y);
            return pA.CompareTo(pB);
        });

        foreach(var pos in loadPositions)
        {
            int dx = pos.X - center.X; int dz = pos.Z - center.Z;
            int priority = dx*dx + dz*dz + Math.Abs(pos.Y);
            toLoad.Add(new ChunkGenerationTask { Position = pos, Priority = priority });
        }

        if (toUnload.Count > 0) _incomingChunksToUnload.Enqueue(toUnload);
        if (toLoad.Count > 0) _incomingChunksToLoad.Enqueue(toLoad);
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
                    _generationQueue.Add(task);
                }
            }
        }
    }

    private void ProcessGeneratedChunks()
    {
        int processed = 0;
        while (processed < 20 && _generatedChunksQueue.TryDequeue(out var result))
        {
            _chunksInProgress.Remove(result.Position);
            
            // Если чанк больше не нужен (игрок ушел), всё равно надо вернуть массив!
            if (!_activeChunkPositions.Contains(result.Position)) 
            {
                ArrayPool<MaterialType>.Shared.Return(result.Voxels);
                continue;
            }

            lock (_chunksLock)
            {
                if (_chunks.ContainsKey(result.Position)) 
                {
                    ArrayPool<MaterialType>.Shared.Return(result.Voxels);
                    continue;
                }
                
                var chunk = new Chunk(result.Position, this);
                chunk.SetDataFromArray(result.Voxels); // Копирует данные во внутренний byte[] чанка
                _chunks[result.Position] = chunk;

                _physicsBuildQueue.Add(new PhysicsBuildTask { ChunkToProcess = chunk });
                OnChunkLoaded?.Invoke(chunk);
            }
            
            // --- ВОЗВРАЩАЕМ МАССИВ В ПУЛ ---
            ArrayPool<MaterialType>.Shared.Return(result.Voxels);
            
            processed++;
        }
    }

    private void ProcessPhysicsResults()
    {
        long startTime = Stopwatch.GetTimestamp();
        long maxTicks = (long)(0.003 * Stopwatch.Frequency); 

        while (_physicsResultQueue.TryDequeue(out var result))
        {
            if (result.TargetChunk == null || !result.TargetChunk.IsLoaded) continue;

            StaticHandle handle = default;
            if (result.Colliders != null && result.Colliders.Count > 0)
            {
                handle = PhysicsWorld.AddStaticChunkBody(
                    (result.TargetChunk.Position * Chunk.ChunkSize).ToSystemNumerics(),
                    result.Colliders
                );
            }
            result.TargetChunk.OnPhysicsRebuilt(handle);

            if (Stopwatch.GetTimestamp() - startTime > maxTicks) break;
        }
    }

    public void ProcessVoxelObjects()
    {
        while (_voxelObjectsToAdd.TryDequeue(out var vo)) _voxelObjects.Add(vo);
        foreach (var vo in _voxelObjects)
        {
            if (PhysicsWorld.Simulation.Bodies.BodyExists(vo.BodyHandle))
            {
                var pose = PhysicsWorld.GetPose(vo.BodyHandle);
                vo.UpdatePose(pose);
            }
        }
    }

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
            catch {}
        }
        _objectsToRemove.Clear();
    }

    #endregion

    #region Public API

    // --- ВОТ ЭТОГО МЕТОДА НЕ ХВАТАЛО ---
    public void NotifyVoxelFastDestroyed(Vector3i worldPos)
    {
        OnVoxelFastDestroyed?.Invoke(worldPos);
    }

    public void NotifyChunkModified(Chunk chunk)
    {
        OnChunkModified?.Invoke(chunk);
    }

    public int GetViewRangeInVoxels() => GameSettings.RenderDistance * Chunk.ChunkSize;

    public Vector3 GetPlayerPosition() => PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle)
        .Pose.Position.ToOpenTK();

    public List<Chunk> GetChunksSnapshot()
    {
        lock (_chunksLock) return new List<Chunk>(_chunks.Values);
    }

    public void QueueDetachmentCheck(Chunk chunk, Vector3i localPosition)
    {
        _detachmentQueue.Add(new DetachmentCheckTask { Chunk = chunk, RemovedVoxelLocalPos = localPosition });
    }

    public void DestroyVoxelAt(CollidableReference collidable, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        if (_staticToChunkMap.TryGetValue(collidable.StaticHandle, out var chunk))
        {
            var pointInsideVoxel = worldHitLocation - worldHitNormal * 0.001f;
            var voxelWorldPos = new Vector3i((int)Math.Floor(pointInsideVoxel.X), (int)Math.Floor(pointInsideVoxel.Y),
                (int)Math.Floor(pointInsideVoxel.Z));
            var chunkWorldOrigin = chunk.Position * Chunk.ChunkSize;
            var voxelToRemove = new Vector3i(voxelWorldPos.X - chunkWorldOrigin.X, voxelWorldPos.Y - chunkWorldOrigin.Y,
                voxelWorldPos.Z - chunkWorldOrigin.Z);

            if (chunk.RemoveVoxelAndUpdate(voxelToRemove))
            {
                OnVoxelFastDestroyed?.Invoke(chunk.Position * Chunk.ChunkSize + voxelToRemove);
            }
        }
    }

    public void RegisterChunkStatic(StaticHandle handle, Chunk chunk)
    {
        lock (_chunksLock) _staticToChunkMap[handle] = chunk;
    }

    public void UnregisterChunkStatic(StaticHandle handle)
    {
        lock (_chunksLock) _staticToChunkMap.Remove(handle);
    }

    public void QueueForRemoval(VoxelObject obj) => _objectsToRemove.Add(obj);

    #endregion

    #region Cleanup

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

    public void Dispose()
    {
        _isDisposed = true;
        _genCts.Cancel(); 
        
        _generationQueue.CompleteAdding();
        _physicsBuildQueue.CompleteAdding();
        _detachmentQueue.CompleteAdding();

        foreach(var t in _generationThreads) if (t.IsAlive) t.Join(100);
        if (_physicsBuilderThread.IsAlive) _physicsBuilderThread.Join(100);
        if (_detachmentThread.IsAlive) _detachmentThread.Join(100);

        lock (_chunksLock)
        {
            foreach (var chunk in _chunks.Values) chunk.Dispose();
            _chunks.Clear();
        }

        _generationQueue.Dispose();
        _physicsBuildQueue.Dispose();
        _detachmentQueue.Dispose();
    }
    #endregion
}