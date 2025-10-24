// /World/WorldManager.cs - FINAL STABLE ARCHITECTURE
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
    public int Priority; // Чем меньше - тем важнее
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
    public Compound? CompoundShape; // Nullable, так как форма может не создаться
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

    // === QUEUES ===
    private readonly BlockingCollection<ChunkGenerationTask> _generationQueue = new(new ConcurrentQueue<ChunkGenerationTask>());
    private readonly ConcurrentQueue<ChunkGenerationResult> _generatedChunksQueue = new();

    private readonly BlockingCollection<MeshGenerationTask> _meshQueue = new(new ConcurrentQueue<MeshGenerationTask>());
    private readonly ConcurrentQueue<FinalizedMeshData> _finalizedMeshQueue = new();
    private readonly ConcurrentQueue<FinalizedMeshData> _meshDataPool = new();

    private readonly BlockingCollection<Chunk> _physicsBuildQueue = new(new ConcurrentQueue<Chunk>());
    private readonly ConcurrentQueue<PhysicsBuildResult> _physicsResultQueue = new ConcurrentQueue<PhysicsBuildResult>();

    private readonly BlockingCollection<DetachmentCheckTask> _detachmentQueue = new(new ConcurrentQueue<DetachmentCheckTask>());
    private readonly ConcurrentQueue<DetachmentResult> _detachmentResultQueue = new();

    // === SYNCHRONIZATION ===
    private readonly object _chunksLock = new();
    private readonly HashSet<Vector3i> _chunksInProgress = new();
    private readonly HashSet<Vector3i> _activeChunkPositions = new();
    private readonly Dictionary<Vector3i, FinalizedMeshData> _pendingMeshes = new();

    private Vector3i _lastPlayerChunkPosition = new(int.MaxValue);

    private int _viewDistance = 16;
    private const int MaxChunksPerFrame = 32;
    private const int MaxPhysicsRebuildsPerFrame = 16; // Теперь это лимит на ПРИМЕНЕНИЕ результатов
    private const int MaxMeshUpdatesPerFrame = 16;

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
        public readonly List<float> Vertices = new List<float>(50000);
        public readonly List<float> Colors = new List<float>(50000);
        public readonly List<float> AoValues = new List<float>(17000);
        private readonly WorldManager _worldManager;
        private Vector3i _chunkPosition;
        public MeshBuilderContext(WorldManager worldManager) { _worldManager = worldManager; }
        public void PrepareFor(Vector3i chunkPosition) { _chunkPosition = chunkPosition; Vertices.Clear(); Colors.Clear(); AoValues.Clear(); }
        public bool IsVoxelSolidForMesh(Vector3i localPos)
        {
            Vector3i worldPos = (_chunkPosition * Chunk.ChunkSize) + localPos;
            return _worldManager.IsVoxelSolidWorld(worldPos);
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

                    stopwatch.Stop(); // <--- Останавливаем таймер
                    PerformanceMonitor.RecordTiming(ThreadType.Generation, stopwatch); // <--- Записываем результат
                }
            }
            catch (Exception ex) { Console.WriteLine($"[GenerationThread] Error: {ex.Message}"); }
        }
        Console.WriteLine("[GenerationThread] Stopped.");
    }

    private void MeshBuilderThreadLoop()
    {
        Console.WriteLine("[MeshBuilderThread] Started.");
        var context = new MeshBuilderContext(this);
        var stopwatch = new Stopwatch();
        while (!_isDisposed)
        {
            try
            {
                if (_meshQueue.TryTake(out var task, 50))
                {
                    stopwatch.Restart();

                    while (_finalizedMeshQueue.Count > 40 && !_isDisposed)
                    {
                        Thread.Sleep(10);
                    }
                    if (task.ChunkToProcess == null || !task.ChunkToProcess.IsLoaded) continue;

                    Dictionary<Vector3i, MaterialType> voxels;
                    lock (task.ChunkToProcess.VoxelsLock)
                    {
                        voxels = task.ChunkToProcess.Voxels;
                    }

                    context.PrepareFor(task.ChunkToProcess.Position);
                    if (voxels.Count > 0)
                    {
                        VoxelMeshBuilder.GenerateMesh(voxels, context.Vertices, context.Colors, context.AoValues, context.IsVoxelSolidForMesh);
                    }

                    if (!_meshDataPool.TryDequeue(out var meshData))
                    {
                        meshData = new FinalizedMeshData();
                    }

                    meshData.ChunkPosition = task.ChunkToProcess.Position;
                    meshData.Vertices.Clear();
                    meshData.Vertices.AddRange(context.Vertices);
                    meshData.Colors.Clear();
                    meshData.Colors.AddRange(context.Colors);
                    meshData.AoValues.Clear();
                    meshData.AoValues.AddRange(context.AoValues);
                    meshData.IsUpdate = task.IsUpdate;

                    _finalizedMeshQueue.Enqueue(meshData);

                    stopwatch.Stop(); // <--- Останавливаем таймер
                    PerformanceMonitor.RecordTiming(ThreadType.Meshing, stopwatch); // <--- Записываем результат
                }
            }
            catch (Exception ex) { Console.WriteLine($"[MeshBuilderThread] Error: {ex.Message}"); }
        }
        Console.WriteLine("[MeshBuilderThread] Stopped.");
    }

    private void PhysicsBuilderThreadLoop()
    {
        Console.WriteLine("[PhysicsBuilderThread] Started.");
        //Точка диагностики
        var stopwatch = new Stopwatch();
        while (!_isDisposed)
        {
            try
            {
                if (_physicsBuildQueue.TryTake(out var chunkToRebuild, 50))
                {
                    stopwatch.Restart(); // <--- Запускаем таймер

                    if (chunkToRebuild == null || !chunkToRebuild.IsLoaded) continue;

                    Dictionary<Vector3i, MaterialType> surfaceVoxels = chunkToRebuild.GetSurfaceVoxels();
                    var compoundShape = PhysicsWorld.CreateStaticChunkShape(surfaceVoxels, out var childrenBuffer);

                    _physicsResultQueue.Enqueue(new PhysicsBuildResult
                    {
                        TargetChunk = chunkToRebuild,
                        CompoundShape = compoundShape,
                        ChildrenBuffer = childrenBuffer
                    });

                    stopwatch.Stop(); // <--- Останавливаем таймер
                    PerformanceMonitor.RecordTiming(ThreadType.Physics, stopwatch); // <--- Записываем результат
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

                    stopwatch.Stop(); // <--- Останавливаем таймер
                    PerformanceMonitor.RecordTiming(ThreadType.Detachment, stopwatch); // <--- Записываем результат
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
            if (_meshQueue.Count > 40) // Backpressure for mesh queue
            {
                _generatedChunksQueue.Enqueue(result);
                break;
            }
            processed++;
            _chunksInProgress.Remove(result.Position);
            if (!_activeChunkPositions.Contains(result.Position)) continue;
            lock (_chunksLock)
            {
                if (_chunks.ContainsKey(result.Position)) continue;
                var chunk = new Chunk(result.Position, this);
                chunk.SetVoxelData(result.Voxels);
                _chunks[result.Position] = chunk;

                // Add to both processing queues
                _physicsBuildQueue.Add(chunk);
                _meshQueue.Add(new MeshGenerationTask { ChunkToProcess = chunk, IsUpdate = false });
            }
        }
    }

    private void ProcessFinalizedMeshes()
    {
        int processedMeshes = 0;
        while (processedMeshes < MaxMeshUpdatesPerFrame && _finalizedMeshQueue.TryDequeue(out var data))
        {
            processedMeshes++;
            bool wasPending = false;
            lock (_chunksLock)
            {
                if (_chunks.TryGetValue(data.ChunkPosition, out var chunk))
                {
                    if (chunk.HasPhysics)
                    {
                        chunk.ApplyMesh(data.Vertices, data.Colors, data.AoValues);
                    }
                    else
                    {
                        _pendingMeshes[data.ChunkPosition] = data;
                        wasPending = true;
                    }
                }
            }
            if (!wasPending)
            {
                _meshDataPool.Enqueue(data);
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

            result.TargetChunk.OnPhysicsRebuilt(handle);

            var chunkPos = result.TargetChunk.Position;
            FinalizedMeshData meshData = null;
            lock (_chunksLock)
            {
                if (_pendingMeshes.TryGetValue(chunkPos, out meshData))
                {
                    _pendingMeshes.Remove(chunkPos);
                }
            }
            if (meshData != null)
            {
                result.TargetChunk.ApplyMesh(meshData.Vertices, meshData.Colors, meshData.AoValues);
                _meshDataPool.Enqueue(meshData);
            }
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
                RebuildChunkMeshAsync(result.OriginChunk);
                RebuildChunkPhysicsAsync(result.OriginChunk);
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

    public void RebuildChunkMeshAsync(Chunk chunk)
    {
        if (chunk == null || !chunk.IsLoaded) return;
        _meshQueue.Add(new MeshGenerationTask { ChunkToProcess = chunk, IsUpdate = true });
    }

    public void RebuildChunkPhysicsAsync(Chunk chunk)
    {
        if (chunk == null || !chunk.IsLoaded) return;
        _physicsBuildQueue.Add(chunk);
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
                    RebuildChunkMeshAsync(neighbor);
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

    public void Debug_PrintChunkStates()
    {
        Console.WriteLine("\n===== CHUNK DEBUG DUMP =====");
        lock (_chunksLock)
        {
            Console.WriteLine($"Total Chunks in Dictionary: {_chunks.Count}");
            foreach (var pair in _chunks)
            {
                var pos = pair.Key;
                var chunk = pair.Value;
                Console.WriteLine($"-- Chunk at {pos}:");
                Console.WriteLine($"   IsLoaded: {chunk.IsLoaded}");
                Console.WriteLine($"   HasPhysics: {chunk.HasPhysics}");
                Console.WriteLine($"   Voxel Count: {chunk.Voxels.Count}");
                Console.WriteLine($"   Static Handles Count: {chunk.StaticHandlesCount}");
            }
        }

        Console.WriteLine("\n--- System Maps ---");
        Console.WriteLine($"_staticToChunkMap Count: {_staticToChunkMap.Count}");
        Console.WriteLine($"_pendingMeshes Count: {_pendingMeshes.Count}");
        if (_pendingMeshes.Count > 0)
        {
            var keys = string.Join(", ", _pendingMeshes.Keys);
            Console.WriteLine($"Pending meshes for chunks: [{keys}]");
        }
        Console.WriteLine("==========================\n");
    }

}