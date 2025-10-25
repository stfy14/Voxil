// /World/WorldManager.cs - OPTIMIZED VERSION
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using BepuUtilities.Memory;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BepuVector3 = System.Numerics.Vector3;

#region Task Structures

public class ChunkGenerationTask
{
    public Vector3i Position;
    public int Priority;
}

public class ChunkGenerationResult
{
    public Vector3i Position;
    public Dictionary<Vector3i, MaterialType> Voxels;
}

public class MeshGenerationTask
{
    public Chunk ChunkToProcess;
    public bool IsUpdate;
    // --- ИЗМЕНЕНИЕ: Храним ссылки на чанки, а не копии данных ---
    public Chunk NeighborChunk_NX;
    public Chunk NeighborChunk_PX;
    public Chunk NeighborChunk_NZ;
    public Chunk NeighborChunk_PZ;
}

public class PhysicsBuildTask
{
    public Chunk ChunkToProcess;
    // --- ИЗМЕНЕНИЕ: Храним ссылки на чанки, а не копии данных ---
    public Chunk NeighborChunk_NX;
    public Chunk NeighborChunk_PX;
    public Chunk NeighborChunk_NZ;
    public Chunk NeighborChunk_PZ;
}


public class FinalizedMeshData
{
    public Vector3i ChunkPosition;
    public List<float> Vertices = new List<float>();
    public List<float> Colors = new List<float>();
    public List<float> AoValues = new List<float>();
    public bool IsUpdate;
}

public class PhysicsBuildResult
{
    public Chunk TargetChunk;
    public Compound? CompoundShape;
    public Buffer<CompoundChild> ChildrenBuffer;
}

public class DetachmentCheckTask
{
    public Chunk Chunk;
    public Vector3i RemovedVoxelLocalPos;
}

public class DetachmentResult
{
    public Chunk OriginChunk;
    public List<Vector3i> DetachedGroup;
    public MaterialType Material;
    public BepuVector3 WorldOffset;
}

#endregion

public class WorldManager : IDisposable
{
    public PhysicsWorld PhysicsWorld { get; }
    private readonly PlayerController _playerController;
    private readonly IWorldGenerator _generator;

    private readonly Dictionary<Vector3i, Chunk> _chunks = new();
    private readonly Dictionary<BodyHandle, VoxelObject> _bodyToVoxelObjectMap = new();
    private readonly Dictionary<StaticHandle, Chunk> _staticToChunkMap = new();

    private readonly List<VoxelObject> _voxelObjects = new();
    private readonly ConcurrentQueue<VoxelObject> _voxelObjectsToAdd = new();
    private readonly List<VoxelObject> _objectsToRemove = new();

    // === THREADS ===
    private readonly Thread _generationThread;
    private readonly Thread _meshBuilderThread;
    private readonly Thread _detachmentThread;
    private readonly Thread _physicsBuilderThread;
    private volatile bool _isDisposed = false;

    // === QUEUES & POOLS ===
    private readonly BlockingCollection<ChunkGenerationTask> _generationQueue = new(new ConcurrentQueue<ChunkGenerationTask>());
    private readonly ConcurrentQueue<ChunkGenerationResult> _generatedChunksQueue = new();

    private readonly BlockingCollection<MeshGenerationTask> _meshQueue = new(new ConcurrentQueue<MeshGenerationTask>());
    private readonly ConcurrentQueue<FinalizedMeshData> _finalizedMeshQueue = new();
    private readonly ConcurrentQueue<FinalizedMeshData> _meshDataPool = new();

    private readonly BlockingCollection<PhysicsBuildTask> _physicsBuildQueue = new(new ConcurrentQueue<PhysicsBuildTask>());
    private readonly ConcurrentQueue<PhysicsBuildResult> _physicsResultQueue = new ConcurrentQueue<PhysicsBuildResult>();

    private readonly BlockingCollection<DetachmentCheckTask> _detachmentQueue = new(new ConcurrentQueue<DetachmentCheckTask>());
    private readonly ConcurrentQueue<DetachmentResult> _detachmentResultQueue = new();

    // --- NEW: OBJECT POOLS FOR TASKS ---
    private readonly ConcurrentQueue<MeshGenerationTask> _meshTaskPool = new();
    private readonly ConcurrentQueue<PhysicsBuildTask> _physicsTaskPool = new();

    // === SYNCHRONIZATION ===
    private readonly object _chunksLock = new();
    private readonly HashSet<Vector3i> _chunksInProgress = new();
    private readonly HashSet<Vector3i> _activeChunkPositions = new();

    private Vector3i _lastPlayerChunkPosition = new(int.MaxValue);

    private int _viewDistance = 16;
    private const int MaxChunksPerFrame = 64;
    private const int MaxPhysicsRebuildsPerFrame = 32;
    private const int MaxMeshUpdatesPerFrame = 128;

    private float _memoryLogTimer = 0f;

    public WorldManager(PhysicsWorld physicsWorld, PlayerController playerController)
    {
        PhysicsWorld = physicsWorld;
        _playerController = playerController;
        _generator = new PerlinGenerator(12345);

        _generationThread = new Thread(GenerationThreadLoop) { IsBackground = true, Name = "ChunkGeneration", Priority = ThreadPriority.BelowNormal };
        _meshBuilderThread = new Thread(MeshBuilderThreadLoop) { IsBackground = true, Name = "MeshBuilder", Priority = ThreadPriority.Normal };
        _detachmentThread = new Thread(DetachmentThreadLoop) { IsBackground = true, Name = "DetachmentCheck", Priority = ThreadPriority.BelowNormal };
        _physicsBuilderThread = new Thread(PhysicsBuilderThreadLoop) { IsBackground = true, Name = "PhysicsBuilder", Priority = ThreadPriority.BelowNormal };

        _generationThread.Start();
        _meshBuilderThread.Start();
        _detachmentThread.Start();
        _physicsBuilderThread.Start();

        Console.WriteLine("[WorldManager] Initialized with 4 worker threads.");
    }

    #region Thread Loops

    private class MeshBuilderContext
    {
        public List<float> Vertices = new List<float>(50000);
        public List<float> Colors = new List<float>(50000);
        public List<float> AoValues = new List<float>(17000);

        // --- OPTIMIZATION: Reusable dictionary ---
        internal readonly Dictionary<Vector3i, MaterialType> _mainChunkVoxels = new();
        private Dictionary<Vector3i, MaterialType> _neighbor_NX;
        private Dictionary<Vector3i, MaterialType> _neighbor_PX;
        private Dictionary<Vector3i, MaterialType> _neighbor_NZ;
        private Dictionary<Vector3i, MaterialType> _neighbor_PZ;

        public void LoadData(Chunk mainChunk,
                             Dictionary<Vector3i, MaterialType> nx, Dictionary<Vector3i, MaterialType> px,
                             Dictionary<Vector3i, MaterialType> nz, Dictionary<Vector3i, MaterialType> pz)
        {
            // --- OPTIMIZATION: Clear and reuse instead of creating new ---
            _mainChunkVoxels.Clear();
            lock (mainChunk.VoxelsLock)
            {
                // Копируем вручную, чтобы избежать создания нового словаря
                foreach (var kvp in mainChunk.Voxels)
                {
                    _mainChunkVoxels.Add(kvp.Key, kvp.Value);
                }
            }

            _neighbor_NX = nx; _neighbor_PX = px;
            _neighbor_NZ = nz; _neighbor_PZ = pz;

            Vertices.Clear();
            Colors.Clear();
            AoValues.Clear();
        }

        public bool IsVoxelSolidForMesh(Vector3i localPos)
        {
            if (localPos.X >= 0 && localPos.X < Chunk.ChunkSize &&
                localPos.Z >= 0 && localPos.Z < Chunk.ChunkSize &&
                localPos.Y >= 0)
            {
                return _mainChunkVoxels.ContainsKey(localPos);
            }

            if (localPos.X < 0)
                return _neighbor_NX?.ContainsKey(localPos + new Vector3i(Chunk.ChunkSize, 0, 0)) ?? false;
            if (localPos.X >= Chunk.ChunkSize)
                return _neighbor_PX?.ContainsKey(localPos - new Vector3i(Chunk.ChunkSize, 0, 0)) ?? false;
            if (localPos.Z < 0)
                return _neighbor_NZ?.ContainsKey(localPos + new Vector3i(0, 0, Chunk.ChunkSize)) ?? false;
            if (localPos.Z >= Chunk.ChunkSize)
                return _neighbor_PZ?.ContainsKey(localPos - new Vector3i(0, 0, Chunk.ChunkSize)) ?? false;

            return false;
        }
    }

    private void GenerationThreadLoop()
    {
        Console.WriteLine("[GenerationThread] Started.");
        var stopwatch = new Stopwatch();
        while (!_isDisposed)
        {
            try
            {
                if (_generationQueue.TryTake(out var task, 50))
                {
                    stopwatch.Restart();
                    var voxels = new Dictionary<Vector3i, MaterialType>();
                    _generator.GenerateChunk(task.Position, voxels);
                    _generatedChunksQueue.Enqueue(new ChunkGenerationResult { Position = task.Position, Voxels = voxels });
                    stopwatch.Stop();
                    PerformanceMonitor.RecordTiming(ThreadType.Generation, stopwatch);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[GenerationThread] Error: {ex.Message}"); }
        }
        Console.WriteLine("[GenerationThread] Stopped.");
    }

    private void MeshBuilderThreadLoop()
    {
        Console.WriteLine("[MeshBuilderThread] Started.");
        var context = new MeshBuilderContext();
        var stopwatch = new Stopwatch();
        while (!_isDisposed)
        {
            try
            {
                if (_meshQueue.TryTake(out var task, 50))
                {
                    stopwatch.Restart();
                    if (task.ChunkToProcess == null || !task.ChunkToProcess.IsLoaded)
                    {
                        _meshTaskPool.Enqueue(task); // Возвращаем задачу в пул
                        continue;
                    }

                    // --- OPTIMIZATION: Копирование данных перенесено сюда ---
                    var nx = (task.NeighborChunk_NX != null && task.NeighborChunk_NX.IsLoaded) ? new Dictionary<Vector3i, MaterialType>(task.NeighborChunk_NX.Voxels) : null;
                    var px = (task.NeighborChunk_PX != null && task.NeighborChunk_PX.IsLoaded) ? new Dictionary<Vector3i, MaterialType>(task.NeighborChunk_PX.Voxels) : null;
                    var nz = (task.NeighborChunk_NZ != null && task.NeighborChunk_NZ.IsLoaded) ? new Dictionary<Vector3i, MaterialType>(task.NeighborChunk_NZ.Voxels) : null;
                    var pz = (task.NeighborChunk_PZ != null && task.NeighborChunk_PZ.IsLoaded) ? new Dictionary<Vector3i, MaterialType>(task.NeighborChunk_PZ.Voxels) : null;

                    context.LoadData(task.ChunkToProcess, nx, px, nz, pz);

                    if (context._mainChunkVoxels.Count > 0)
                    {
                        VoxelMeshBuilder.GenerateMesh(context._mainChunkVoxels, context.Vertices, context.Colors, context.AoValues, context.IsVoxelSolidForMesh);
                    }

                    if (!_meshDataPool.TryDequeue(out var meshData))
                        meshData = new FinalizedMeshData();

                    meshData.ChunkPosition = task.ChunkToProcess.Position;
                    (meshData.Vertices, context.Vertices) = (context.Vertices, meshData.Vertices);
                    (meshData.Colors, context.Colors) = (context.Colors, meshData.Colors);
                    (meshData.AoValues, context.AoValues) = (context.AoValues, meshData.AoValues);
                    meshData.IsUpdate = task.IsUpdate;

                    _finalizedMeshQueue.Enqueue(meshData);

                    stopwatch.Stop();
                    PerformanceMonitor.RecordTiming(ThreadType.Meshing, stopwatch);
                    _meshTaskPool.Enqueue(task);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[MeshBuilderThread] Error: {ex.Message}"); }
        }
        Console.WriteLine("[MeshBuilderThread] Stopped.");
    }

    private void PhysicsBuilderThreadLoop()
    {
        Console.WriteLine("[PhysicsBuilderThread] Started.");
        var stopwatch = new Stopwatch();
        while (!_isDisposed)
        {
            try
            {
                if (_physicsBuildQueue.TryTake(out var task, 50))
                {
                    stopwatch.Restart();
                    if (task.ChunkToProcess == null || !task.ChunkToProcess.IsLoaded)
                    {
                        _physicsTaskPool.Enqueue(task); // Возвращаем задачу в пул
                        continue;
                    }

                    // --- OPTIMIZATION: Копирование данных перенесено сюда ---
                    var nx = (task.NeighborChunk_NX != null && task.NeighborChunk_NX.IsLoaded) ? new Dictionary<Vector3i, MaterialType>(task.NeighborChunk_NX.Voxels) : null;
                    var px = (task.NeighborChunk_PX != null && task.NeighborChunk_PX.IsLoaded) ? new Dictionary<Vector3i, MaterialType>(task.NeighborChunk_PX.Voxels) : null;
                    var nz = (task.NeighborChunk_NZ != null && task.NeighborChunk_NZ.IsLoaded) ? new Dictionary<Vector3i, MaterialType>(task.NeighborChunk_NZ.Voxels) : null;
                    var pz = (task.NeighborChunk_PZ != null && task.NeighborChunk_PZ.IsLoaded) ? new Dictionary<Vector3i, MaterialType>(task.NeighborChunk_PZ.Voxels) : null;

                    Dictionary<Vector3i, MaterialType> surfaceVoxels = task.ChunkToProcess.GetSurfaceVoxels(nx, px, nz, pz);

                    var compoundShape = PhysicsWorld.CreateStaticChunkShape(surfaceVoxels, out var childrenBuffer);

                    _physicsResultQueue.Enqueue(new PhysicsBuildResult
                    {
                        TargetChunk = task.ChunkToProcess,
                        CompoundShape = compoundShape,
                        ChildrenBuffer = childrenBuffer
                    });

                    stopwatch.Stop();
                    PerformanceMonitor.RecordTiming(ThreadType.Physics, stopwatch);
                    _physicsTaskPool.Enqueue(task);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PhysicsBuilderThread] Error: {ex.Message}");
            }
        }
        Console.WriteLine("[PhysicsBuilderThread] Stopped.");
    }

    private void DetachmentThreadLoop()
    {
        Console.WriteLine("[DetachmentThread] Started.");
        var stopwatch = new Stopwatch();
        while (!_isDisposed)
        {
            try
            {
                if (_detachmentQueue.TryTake(out var task, 50))
                {
                    stopwatch.Restart();
                    ProcessDetachment(task);
                    stopwatch.Stop();
                    PerformanceMonitor.RecordTiming(ThreadType.Detachment, stopwatch);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[DetachmentThread] Error: {ex.Message}"); }
        }
        Console.WriteLine("[DetachmentThread] Stopped.");
    }

    private void ProcessDetachment(DetachmentCheckTask task)
    {
        var neighbors = new[] { task.RemovedVoxelLocalPos + Vector3i.UnitX, task.RemovedVoxelLocalPos - Vector3i.UnitX, task.RemovedVoxelLocalPos + Vector3i.UnitY, task.RemovedVoxelLocalPos - Vector3i.UnitY, task.RemovedVoxelLocalPos + Vector3i.UnitZ, task.RemovedVoxelLocalPos - Vector3i.UnitZ };
        var processedGroups = new HashSet<Vector3i>();
        Dictionary<Vector3i, MaterialType> voxelsCopy;
        lock (task.Chunk.VoxelsLock) { voxelsCopy = new Dictionary<Vector3i, MaterialType>(task.Chunk.Voxels); }
        foreach (var neighborPos in neighbors)
        {
            if (!voxelsCopy.ContainsKey(neighborPos) || processedGroups.Contains(neighborPos)) continue;
            if (!IsConnectedToGround(task.Chunk, neighborPos, voxelsCopy))
            {
                var detachedGroup = GetConnectedVoxels(neighborPos, voxelsCopy);
                if (detachedGroup.Count > 0 && voxelsCopy.TryGetValue(detachedGroup[0], out var mat))
                {
                    var chunkWorldPos = (task.Chunk.Position * Chunk.ChunkSize).ToSystemNumerics();
                    _detachmentResultQueue.Enqueue(new DetachmentResult { OriginChunk = task.Chunk, DetachedGroup = detachedGroup, Material = mat, WorldOffset = chunkWorldPos });
                    foreach (var voxel in detachedGroup) processedGroups.Add(voxel);
                }
            }
        }
    }

    #endregion

    #region Main Update

    public void Update(float deltaTime)
    {
        UpdateVisibleChunks();
        ProcessGeneratedChunks();
        ProcessFinalizedMeshes();
        ProcessPhysicsResults();
        ProcessDetachmentResults();
        ProcessVoxelObjects();
        ProcessRemovals();

        _memoryLogTimer += deltaTime;
        if (_memoryLogTimer >= 5.0f)
        {
            lock (_chunksLock)
            {
                var loadedCount = _chunks.Values.Count(c => c.IsLoaded);
                var memoryMB = GC.GetTotalMemory(false) / (1024 * 1024);
                Console.WriteLine($"[World] Chunks: {loadedCount}/{_chunks.Count}, VObjects: {_voxelObjects.Count}, Mem: {memoryMB}MB, " +
                                  $"Queues: Gen={_generationQueue.Count}, Mesh={_meshQueue.Count}, PhysicsBuild={_physicsBuildQueue.Count}");
            }
            _memoryLogTimer = 0f;
        }
    }

    private void UpdateVisibleChunks()
    {
        var playerPosition = PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle).Pose.Position;
        var playerChunkPos = new Vector3i((int)Math.Floor(playerPosition.X / Chunk.ChunkSize), 0, (int)Math.Floor(playerPosition.Z / Chunk.ChunkSize));
        if (playerChunkPos == _lastPlayerChunkPosition) return;
        _lastPlayerChunkPosition = playerChunkPos;
        var newRequiredChunks = new HashSet<Vector3i>();
        for (int x = -_viewDistance; x <= _viewDistance; x++)
        {
            for (int z = -_viewDistance; z <= _viewDistance; z++)
            {
                newRequiredChunks.Add(new Vector3i(playerChunkPos.X + x, 0, playerChunkPos.Z + z));
            }
        }
        var chunksToUnload = new List<Vector3i>();
        lock (_chunksLock)
        {
            foreach (var pos in _activeChunkPositions)
            {
                if (!newRequiredChunks.Contains(pos)) chunksToUnload.Add(pos);
            }
        }
        foreach (var pos in chunksToUnload) UnloadChunk(pos);
        var chunksToLoad = newRequiredChunks.Where(pos => !_activeChunkPositions.Contains(pos) && !_chunksInProgress.Contains(pos)).OrderBy(pos => (pos - playerChunkPos).LengthSquared()).ToList();
        foreach (var pos in chunksToLoad)
        {
            _chunksInProgress.Add(pos);
            int priority = (pos - playerChunkPos).LengthSquared();
            _generationQueue.Add(new ChunkGenerationTask { Position = pos, Priority = priority });
        }
        _activeChunkPositions.Clear();
        foreach (var pos in newRequiredChunks) _activeChunkPositions.Add(pos);
    }

    private void ProcessGeneratedChunks()
    {
        int processed = 0;
        while (processed < MaxChunksPerFrame && _generatedChunksQueue.TryDequeue(out var result))
        {
            if (_meshQueue.Count > 256)
            {
                _generatedChunksQueue.Enqueue(result);
                break;
            }
            processed++;
            _chunksInProgress.Remove(result.Position);
            if (!_activeChunkPositions.Contains(result.Position)) continue;

            Chunk newChunk = null;
            lock (_chunksLock)
            {
                if (_chunks.ContainsKey(result.Position)) continue;
                var chunk = new Chunk(result.Position, this);
                chunk.SetVoxelData(result.Voxels);
                _chunks[result.Position] = chunk;
                newChunk = chunk;
            }

            // Ставим в очередь на перестройку только что созданный чанк
            RebuildChunkAsync(newChunk, false);

            // Уведомляем соседей, что появился новый чанк, и им нужно обновить свои границы
            var offsets = new Vector3i[] { new(-1, 0, 0), new(1, 0, 0), new(0, 0, -1), new(0, 0, 1) };
            foreach (var offset in offsets)
            {
                lock (_chunksLock)
                {
                    if (_chunks.TryGetValue(newChunk.Position + offset, out var neighbor))
                    {
                        RebuildChunkAsync(neighbor); // Вызываем без второго параметра, будет isUpdate = true
                    }
                }
            }
        }
    }

    private Dictionary<Vector3i, MaterialType> GetVoxelData(Chunk sourceChunk, Vector3i offset)
    {
        lock (_chunksLock)
        {
            if (_chunks.TryGetValue(sourceChunk.Position + offset, out var neighbor) && neighbor.IsLoaded)
            {
                lock (neighbor.VoxelsLock)
                {
                    return new Dictionary<Vector3i, MaterialType>(neighbor.Voxels);
                }
            }
        }
        return null;
    }

    private void ProcessFinalizedMeshes()
    {
        int processedMeshes = 0;
        while (processedMeshes < MaxMeshUpdatesPerFrame && _finalizedMeshQueue.TryDequeue(out var data))
        {
            processedMeshes++;
            lock (_chunksLock)
            {
                if (_chunks.TryGetValue(data.ChunkPosition, out var chunk))
                {
                    chunk.TrySetMesh(data, _meshDataPool);
                }
                else
                {
                    _meshDataPool.Enqueue(data);
                }
            }
        }
    }

    private void ProcessPhysicsResults()
    {
        int processed = 0;
        while (processed < MaxPhysicsRebuildsPerFrame && _physicsResultQueue.TryDequeue(out var result))
        {
            if (result.TargetChunk == null || !result.TargetChunk.IsLoaded)
            {
                if (result.ChildrenBuffer.Allocated)
                    PhysicsWorld.Simulation.BufferPool.Return(ref result.ChildrenBuffer);
                continue;
            }
            processed++;

            StaticHandle handle = default;
            if (result.CompoundShape.HasValue)
            {
                handle = PhysicsWorld.AddStaticChunkBody(
                    (result.TargetChunk.Position * Chunk.ChunkSize).ToSystemNumerics(),
                    result.CompoundShape.Value,
                    result.ChildrenBuffer);
            }

            result.TargetChunk.OnPhysicsRebuilt(handle, _meshDataPool);
        }
    }

    private void ProcessDetachmentResults()
    {
        while (_detachmentResultQueue.TryDequeue(out var result))
        {
            if (result.OriginChunk == null || !result.OriginChunk.IsLoaded) continue;
            bool removedAny = false;
            lock (result.OriginChunk.VoxelsLock)
            {
                foreach (var voxel in result.DetachedGroup) { if (result.OriginChunk.Voxels.Remove(voxel)) removedAny = true; }
            }
            if (removedAny)
            {
                Console.WriteLine($"[WorldManager] Detached {result.DetachedGroup.Count} voxels, creating dynamic object...");

                RebuildChunkAsync(result.OriginChunk);

                var centerSum = BepuVector3.Zero;
                foreach (var voxel in result.DetachedGroup) { centerSum += new BepuVector3(voxel.X, voxel.Y, voxel.Z); }
                centerSum /= result.DetachedGroup.Count;
                var worldPosition = result.WorldOffset + centerSum;
                CreateDetachedVoxelObject(result.DetachedGroup, result.Material, worldPosition);
            }
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
        if (_objectsToRemove.Count == 0) return;
        foreach (var obj in _objectsToRemove)
        {
            if (obj == null) continue;
            try
            {
                _bodyToVoxelObjectMap.Remove(obj.BodyHandle);
                _voxelObjects.Remove(obj);
                PhysicsWorld.RemoveBody(obj.BodyHandle);
                obj.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorldManager] Error removing voxel object: {ex.Message}\n{ex.StackTrace}");
            }
        }
        _objectsToRemove.Clear();
    }

    #endregion

    #region Public API

    public void RebuildChunkAsync(Chunk chunk, bool isUpdate = true)
    {
        if (chunk == null || !chunk.IsLoaded) return;

        // --- OPTIMIZATION: Этот метод теперь супер-быстрый ---
        // Он больше не копирует словари, а только находит ссылки на соседей.
        Chunk n_nx, n_px, n_nz, n_pz;
        lock (_chunksLock)
        {
            _chunks.TryGetValue(chunk.Position + new Vector3i(-1, 0, 0), out n_nx);
            _chunks.TryGetValue(chunk.Position + new Vector3i(1, 0, 0), out n_px);
            _chunks.TryGetValue(chunk.Position + new Vector3i(0, 0, -1), out n_nz);
            _chunks.TryGetValue(chunk.Position + new Vector3i(0, 0, 1), out n_pz);
        }

        if (!_meshTaskPool.TryDequeue(out var meshTask))
            meshTask = new MeshGenerationTask();

        meshTask.ChunkToProcess = chunk;
        meshTask.IsUpdate = isUpdate;
        meshTask.NeighborChunk_NX = n_nx;
        meshTask.NeighborChunk_PX = n_px;
        meshTask.NeighborChunk_NZ = n_nz;
        meshTask.NeighborChunk_PZ = n_pz;
        _meshQueue.Add(meshTask);

        if (!_physicsTaskPool.TryDequeue(out var physicsTask))
            physicsTask = new PhysicsBuildTask();

        physicsTask.ChunkToProcess = chunk;
        physicsTask.NeighborChunk_NX = n_nx;
        physicsTask.NeighborChunk_PX = n_px;
        physicsTask.NeighborChunk_NZ = n_nz;
        physicsTask.NeighborChunk_PZ = n_pz;
        _physicsBuildQueue.Add(physicsTask);
    }

    public void QueueDetachmentCheck(Chunk chunk, Vector3i removedVoxelLocalPos)
    {
        _detachmentQueue.Add(new DetachmentCheckTask { Chunk = chunk, RemovedVoxelLocalPos = removedVoxelLocalPos });
    }

    public bool IsVoxelSolidWorld(Vector3i worldPos)
    {
        var chunkPos = new Vector3i((int)Math.Floor(worldPos.X / (float)Chunk.ChunkSize), 0, (int)Math.Floor(worldPos.Z / (float)Chunk.ChunkSize));
        lock (_chunksLock)
        {
            if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded)
            {
                var localPos = new Vector3i(worldPos.X - chunk.Position.X * Chunk.ChunkSize, worldPos.Y, worldPos.Z - chunk.Position.Z * Chunk.ChunkSize);
                lock (chunk.VoxelsLock)
                {
                    return chunk.Voxels.ContainsKey(localPos);
                }
            }
        }
        return false;
    }

    public void DestroyVoxelAt(CollidableReference collidable, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        if (collidable.Mobility == CollidableMobility.Dynamic)
        {
            if (_bodyToVoxelObjectMap.TryGetValue(collidable.BodyHandle, out var dynamicObject))
                DestroyDynamicVoxelAt(dynamicObject, worldHitLocation, worldHitNormal);
            return;
        }

        if (_staticToChunkMap.TryGetValue(collidable.StaticHandle, out var chunk))
        {
            var pointInsideVoxel = worldHitLocation - worldHitNormal * 0.001f;
            var voxelWorldPos = new Vector3i((int)Math.Floor(pointInsideVoxel.X), (int)Math.Floor(pointInsideVoxel.Y), (int)Math.Floor(pointInsideVoxel.Z));
            var chunkWorldOrigin = chunk.Position * Chunk.ChunkSize;
            var voxelToRemove = new Vector3i(voxelWorldPos.X - chunkWorldOrigin.X, voxelWorldPos.Y - chunkWorldOrigin.Y, voxelWorldPos.Z - chunkWorldOrigin.Z);
            bool containsKey;
            lock (chunk.VoxelsLock) { containsKey = chunk.Voxels.ContainsKey(voxelToRemove); }
            if (containsKey)
            {
                chunk.RemoveVoxelAndUpdate(voxelToRemove);
            }
        }
    }

    public void NotifyNeighborsOfVoxelChange(Vector3i chunkPos, Vector3i localVoxelPos)
    {
        Action<Vector3i> rebuildNeighbor = offset =>
        {
            lock (_chunksLock)
            {
                if (_chunks.TryGetValue(chunkPos + offset, out var neighbor))
                {
                    RebuildChunkAsync(neighbor); // Вызываем наш единый метод
                }
            }
        };
        if (localVoxelPos.X == 0) rebuildNeighbor(new Vector3i(-1, 0, 0));
        else if (localVoxelPos.X == Chunk.ChunkSize - 1) rebuildNeighbor(new Vector3i(1, 0, 0));
        if (localVoxelPos.Z == 0) rebuildNeighbor(new Vector3i(0, 0, -1));
        else if (localVoxelPos.Z == Chunk.ChunkSize - 1) rebuildNeighbor(new Vector3i(0, 0, 1));
    }

    #endregion

    #region Private Helpers

    private void UnloadChunk(Vector3i position)
    {
        lock (_chunksLock)
        {
            if (_chunks.TryGetValue(position, out var chunk))
            {
                try
                {
                    chunk.Dispose();
                    _chunks.Remove(position);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WorldManager] Error unloading chunk {position}: {ex.Message}");
                }
            }
        }
        _chunksInProgress.Remove(position);
        _activeChunkPositions.Remove(position);
    }

    private void CreateDetachedVoxelObject(List<Vector3i> localCoords, MaterialType material, BepuVector3 worldPosition)
    {
        if (localCoords == null || localCoords.Count == 0) return;
        try
        {
            var newObject = new VoxelObject(localCoords, material, this);
            var handle = PhysicsWorld.CreateVoxelObjectBody(localCoords, material, worldPosition, out var centerOfMass);
            if (!PhysicsWorld.Simulation.Bodies.BodyExists(handle)) return;
            newObject.InitializePhysics(handle, centerOfMass.ToOpenTK());
            newObject.BuildMesh();
            _voxelObjectsToAdd.Enqueue(newObject);
            RegisterVoxelObjectBody(handle, newObject);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WorldManager] Error creating detached object: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private bool IsConnectedToGround(Chunk startChunk, Vector3i startVoxel, Dictionary<Vector3i, MaterialType> voxels)
    {
        var visited = new HashSet<Vector3i>();
        var queue = new Queue<Vector3i>();
        queue.Enqueue(startVoxel);
        visited.Add(startVoxel);
        var directions = new[] { Vector3i.UnitX, -Vector3i.UnitX, Vector3i.UnitY, -Vector3i.UnitY, Vector3i.UnitZ, -Vector3i.UnitZ };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Y == 0) return true;
            foreach (var dir in directions)
            {
                var neighbor = current + dir;
                if (visited.Contains(neighbor)) continue;
                if (voxels.ContainsKey(neighbor))
                {
                    visited.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
            }
        }
        return false;
    }

    private List<Vector3i> GetConnectedVoxels(Vector3i startVoxel, Dictionary<Vector3i, MaterialType> voxels)
    {
        var result = new List<Vector3i>();
        var visited = new HashSet<Vector3i>();
        var queue = new Queue<Vector3i>();
        queue.Enqueue(startVoxel);
        visited.Add(startVoxel);
        result.Add(startVoxel);
        var directions = new[] { Vector3i.UnitX, -Vector3i.UnitX, Vector3i.UnitY, -Vector3i.UnitY, Vector3i.UnitZ, -Vector3i.UnitZ };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var dir in directions)
            {
                var neighbor = current + dir;
                if (!visited.Contains(neighbor) && voxels.ContainsKey(neighbor))
                {
                    visited.Add(neighbor);
                    queue.Enqueue(neighbor);
                    result.Add(neighbor);
                }
            }
        }
        return result;
    }

    private void DestroyDynamicVoxelAt(VoxelObject targetObject, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        if (!PhysicsWorld.Simulation.Bodies.BodyExists(targetObject.BodyHandle))
        {
            QueueForRemoval(targetObject);
            return;
        }
        var pose = PhysicsWorld.GetPose(targetObject.BodyHandle);
        var invOrientation = System.Numerics.Quaternion.Inverse(pose.Orientation);
        var localHitPoint = BepuVector3.Transform(worldHitLocation - pose.Position, invOrientation) + targetObject.LocalCenterOfMass.ToSystemNumerics();
        var localNormal = BepuVector3.Transform(worldHitNormal, invOrientation);
        var voxelToRemove = new Vector3i((int)Math.Floor(localHitPoint.X), (int)Math.Floor(localHitPoint.Y), (int)Math.Floor(localHitPoint.Z));
        if (localNormal.X < -0.9f) voxelToRemove.X--;
        if (localNormal.Y < -0.9f) voxelToRemove.Y--;
        if (localNormal.Z < -0.9f) voxelToRemove.Z--;
        if (!targetObject.VoxelCoordinates.Contains(voxelToRemove)) return;
        var remainingVoxels = new List<Vector3i>(targetObject.VoxelCoordinates);
        remainingVoxels.Remove(voxelToRemove);
        if (remainingVoxels.Count == 0)
        {
            QueueForRemoval(targetObject);
            return;
        }
        var newVoxelIslands = FindConnectedVoxelIslands(remainingVoxels);
        if (newVoxelIslands.Count == 1)
        {
            targetObject.VoxelCoordinates.Remove(voxelToRemove);
            targetObject.RebuildMeshAndPhysics(PhysicsWorld);
        }
        else
        {
            var originalMaterial = targetObject.Material;
            var originalPose = pose;
            foreach (var island in newVoxelIslands)
            {
                var localIslandCenter = BepuVector3.Zero;
                foreach (var voxel in island) localIslandCenter += new BepuVector3(voxel.X, voxel.Y, voxel.Z);
                localIslandCenter /= island.Count;
                var offsetFromOldCoM = localIslandCenter - targetObject.LocalCenterOfMass.ToSystemNumerics();
                var rotatedOffset = BepuVector3.Transform(offsetFromOldCoM, originalPose.Orientation);
                var newWorldPosition = originalPose.Position + rotatedOffset;
                CreateDetachedVoxelObject(island, originalMaterial, newWorldPosition);
            }
            QueueForRemoval(targetObject);
        }
    }

    public List<List<Vector3i>> FindConnectedVoxelIslands(List<Vector3i> voxels)
    {
        var islands = new List<List<Vector3i>>();
        var voxelsToVisit = new HashSet<Vector3i>(voxels);
        var directions = new[] { Vector3i.UnitX, -Vector3i.UnitX, Vector3i.UnitY, -Vector3i.UnitY, Vector3i.UnitZ, -Vector3i.UnitZ };
        while (voxelsToVisit.Count > 0)
        {
            var newIsland = new List<Vector3i>();
            var queue = new Queue<Vector3i>();
            var firstVoxel = voxelsToVisit.First();
            queue.Enqueue(firstVoxel);
            voxelsToVisit.Remove(firstVoxel);
            newIsland.Add(firstVoxel);
            while (queue.Count > 0)
            {
                var currentVoxel = queue.Dequeue();
                foreach (var dir in directions)
                {
                    var neighbor = currentVoxel + dir;
                    if (voxelsToVisit.Contains(neighbor))
                    {
                        voxelsToVisit.Remove(neighbor);
                        queue.Enqueue(neighbor);
                        newIsland.Add(neighbor);
                    }
                }
            }
            islands.Add(newIsland);
        }
        return islands;
    }

    #endregion

    #region Render & Cleanup

    public void Render(Shader shader, Matrix4 view, Matrix4 projection)
    {
        lock (_chunksLock)
        {
            foreach (var chunk in _chunks.Values)
            {
                chunk.Render(shader, view, projection);
            }
        }
        foreach (var vo in _voxelObjects)
        {
            vo.Render(shader, view, projection);
        }
    }

    public void Dispose()
    {
        Console.WriteLine("[WorldManager] Disposing...");
        _isDisposed = true;

        _generationQueue.CompleteAdding();
        _meshQueue.CompleteAdding();
        _detachmentQueue.CompleteAdding();
        _physicsBuildQueue.CompleteAdding();

        _generationThread?.Join(2000);
        _meshBuilderThread?.Join(2000);
        _detachmentThread?.Join(2000);
        _physicsBuilderThread?.Join(2000);

        var allObjects = new List<VoxelObject>(_voxelObjects);
        foreach (var vo in allObjects)
        {
            QueueForRemoval(vo);
        }
        ProcessRemovals();

        lock (_chunksLock)
        {
            foreach (var chunk in _chunks.Values.ToList())
            {
                try { chunk.Dispose(); } catch { }
            }
            _chunks.Clear();
        }

        _bodyToVoxelObjectMap.Clear();
        _staticToChunkMap.Clear();
        _chunksInProgress.Clear();
        _activeChunkPositions.Clear();

        _generationQueue.Dispose();
        _meshQueue.Dispose();
        _detachmentQueue.Dispose();
        _physicsBuildQueue.Dispose();

        Console.WriteLine("[WorldManager] Disposed successfully.");
    }

    public void RegisterChunkStatic(StaticHandle handle, Chunk chunk)
    {
        lock (_chunksLock)
        {
            _staticToChunkMap[handle] = chunk;
        }
    }

    public void UnregisterChunkStatic(StaticHandle handle)
    {
        lock (_chunksLock)
        {
            _staticToChunkMap.Remove(handle);
        }
    }

    private void RegisterVoxelObjectBody(BodyHandle handle, VoxelObject voxelObject) => _bodyToVoxelObjectMap[handle] = voxelObject;

    public void QueueForRemoval(VoxelObject obj)
    {
        if (obj != null && !_objectsToRemove.Contains(obj))
            _objectsToRemove.Add(obj);
    }
    #endregion
}