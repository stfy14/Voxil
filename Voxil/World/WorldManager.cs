// /World/WorldManager.cs - REFACTORED
using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    public Vector3i ChunkPosition;
    public Dictionary<Vector3i, MaterialType> Voxels;
    public int Priority;
    public bool IsUpdate; // true если это обновление существующего чанка
}

public class FinalizedMeshData
{
    public Vector3i ChunkPosition;
    public List<float> Vertices;
    public List<float> Colors;
    public List<float> AoValues;
    public bool IsUpdate;
}

public class PhysicsRebuildTask
{
    public Chunk Chunk;
    public int Priority;
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

    private int _viewDistance = 16;
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
    private volatile bool _isDisposed = false;

    // === QUEUES ===
    private readonly BlockingCollection<ChunkGenerationTask> _generationQueue = new(new ConcurrentQueue<ChunkGenerationTask>());
    private readonly ConcurrentQueue<ChunkGenerationResult> _generatedChunksQueue = new();

    // ИСПРАВЛЕНИЕ: Приоритетная очередь для мешей (ближайшие чанки первыми)
    private readonly BlockingCollection<MeshGenerationTask> _meshQueue = new(new ConcurrentQueue<MeshGenerationTask>());
    private readonly ConcurrentQueue<FinalizedMeshData> _finalizedMeshQueue = new();

    private readonly BlockingCollection<DetachmentCheckTask> _detachmentQueue = new(new ConcurrentQueue<DetachmentCheckTask>());
    private readonly ConcurrentQueue<DetachmentResult> _detachmentResultQueue = new();

    // === SYNCHRONIZATION ===
    private readonly object _chunksLock = new();
    private readonly HashSet<Vector3i> _chunksInProgress = new();
    private readonly HashSet<Vector3i> _activeChunkPositions = new();

    // Приоритетная очередь для физики (обрабатываем важные чанки первыми)
    private readonly SortedSet<PhysicsRebuildTask> _physicsRebuildQueue = new(Comparer<PhysicsRebuildTask>.Create((a, b) =>
    {
        int cmp = a.Priority.CompareTo(b.Priority);
        return cmp != 0 ? cmp : a.Chunk.Position.GetHashCode().CompareTo(b.Chunk.Position.GetHashCode());
    }));

    private Vector3i _lastPlayerChunkPosition = new(int.MaxValue);
    private const int MaxChunksPerFrame = 8; // Увеличено с 4
    private const int MaxPhysicsRebuildsPerFrame = 6; // Увеличено с 2
    private const int MaxMeshUpdatesPerFrame = 12; // Увеличено с 6

    private float _memoryLogTimer = 0f;

    public WorldManager(PhysicsWorld physicsWorld, PlayerController playerController)
    {
        PhysicsWorld = physicsWorld;
        _playerController = playerController;
        _generator = new PerlinGenerator(12345);

        _generationThread = new Thread(GenerationThreadLoop) { IsBackground = true, Name = "ChunkGeneration", Priority = ThreadPriority.BelowNormal };
        _meshBuilderThread = new Thread(MeshBuilderThreadLoop) { IsBackground = true, Name = "MeshBuilder", Priority = ThreadPriority.Normal };
        _detachmentThread = new Thread(DetachmentThreadLoop) { IsBackground = true, Name = "DetachmentCheck", Priority = ThreadPriority.BelowNormal };

        _generationThread.Start();
        _meshBuilderThread.Start();
        _detachmentThread.Start();

        Console.WriteLine("[WorldManager] Initialized with 3 worker threads.");
    }

    #region Thread Loops

    private void GenerationThreadLoop()
    {
        Console.WriteLine("[GenerationThread] Started.");
        while (!_isDisposed)
        {
            try
            {
                if (_generationQueue.TryTake(out var task, 50))
                {
                    var voxels = new Dictionary<Vector3i, MaterialType>();
                    _generator.GenerateChunk(task.Position, voxels);
                    _generatedChunksQueue.Enqueue(new ChunkGenerationResult { Position = task.Position, Voxels = voxels });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GenerationThread] Error: {ex.Message}");
            }
        }
        Console.WriteLine("[GenerationThread] Stopped.");
    }

    private void MeshBuilderThreadLoop()
    {
        Console.WriteLine("[MeshBuilderThread] Started.");

        // Буфер для сортировки по приоритету
        var taskBuffer = new List<MeshGenerationTask>();

        while (!_isDisposed)
        {
            try
            {
                // ОПТИМИЗАЦИЯ: Собираем несколько задач и сортируем по приоритету
                if (_meshQueue.TryTake(out var firstTask, 50))
                {
                    taskBuffer.Clear();
                    taskBuffer.Add(firstTask);

                    // Собираем ещё до 10 задач для пакетной обработки
                    while (taskBuffer.Count < 10 && _meshQueue.TryTake(out var additionalTask, 0))
                    {
                        taskBuffer.Add(additionalTask);
                    }

                    // Сортируем: сначала обновления (IsUpdate), потом по приоритету (расстоянию)
                    taskBuffer.Sort((a, b) =>
                    {
                        if (a.IsUpdate != b.IsUpdate)
                            return a.IsUpdate ? -1 : 1; // Обновления первыми
                        return a.Priority.CompareTo(b.Priority); // Ближайшие первыми
                    });

                    // Обрабатываем первую (самую приоритетную) задачу
                    var task = taskBuffer[0];

                    // Создаём функцию проверки соседей для AO
                    Func<Vector3i, bool> isSolidFunc = localPos =>
                    {
                        Vector3i worldPos = (task.ChunkPosition * Chunk.ChunkSize) + localPos;
                        return IsVoxelSolidWorld(worldPos);
                    };

                    VoxelMeshBuilder.GenerateMesh(task.Voxels, out var vertices, out var colors, out var aoValues, isSolidFunc);

                    _finalizedMeshQueue.Enqueue(new FinalizedMeshData
                    {
                        ChunkPosition = task.ChunkPosition,
                        Vertices = vertices,
                        Colors = colors,
                        AoValues = aoValues,
                        IsUpdate = task.IsUpdate
                    });

                    // Возвращаем остальные задачи в очередь
                    for (int i = 1; i < taskBuffer.Count; i++)
                    {
                        _meshQueue.Add(taskBuffer[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MeshBuilderThread] Error: {ex.Message}");
            }
        }
        Console.WriteLine("[MeshBuilderThread] Stopped.");
    }

    private void DetachmentThreadLoop()
    {
        Console.WriteLine("[DetachmentThread] Started.");
        while (!_isDisposed)
        {
            try
            {
                if (_detachmentQueue.TryTake(out var task, 50))
                {
                    ProcessDetachment(task);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DetachmentThread] Error: {ex.Message}");
            }
        }
        Console.WriteLine("[DetachmentThread] Stopped.");
    }

    private void ProcessDetachment(DetachmentCheckTask task)
    {
        var neighbors = new[]
        {
            task.RemovedVoxelLocalPos + new Vector3i(1, 0, 0),
            task.RemovedVoxelLocalPos + new Vector3i(-1, 0, 0),
            task.RemovedVoxelLocalPos + new Vector3i(0, 1, 0),
            task.RemovedVoxelLocalPos + new Vector3i(0, -1, 0),
            task.RemovedVoxelLocalPos + new Vector3i(0, 0, 1),
            task.RemovedVoxelLocalPos + new Vector3i(0, 0, -1)
        };

        var processedGroups = new HashSet<Vector3i>();

        // КРИТИЧЕСКОЕ ИЗМЕНЕНИЕ: Копируем словарь для безопасной работы
        Dictionary<Vector3i, MaterialType> voxelsCopy;
        lock (task.Chunk.VoxelsLock)
        {
            voxelsCopy = new Dictionary<Vector3i, MaterialType>(task.Chunk.Voxels);
        }

        foreach (var neighborPos in neighbors)
        {
            if (!voxelsCopy.ContainsKey(neighborPos) || processedGroups.Contains(neighborPos))
                continue;

            if (!IsConnectedToGround(task.Chunk, neighborPos, voxelsCopy))
            {
                var detachedGroup = GetConnectedVoxels(neighborPos, voxelsCopy);

                if (detachedGroup.Count > 0 && voxelsCopy.TryGetValue(detachedGroup[0], out var mat))
                {
                    // Вычисляем смещение для правильного позиционирования
                    var chunkWorldPos = (task.Chunk.Position * Chunk.ChunkSize).ToSystemNumerics();

                    _detachmentResultQueue.Enqueue(new DetachmentResult
                    {
                        OriginChunk = task.Chunk,
                        DetachedGroup = detachedGroup,
                        Material = mat,
                        WorldOffset = chunkWorldPos
                    });

                    foreach (var voxel in detachedGroup)
                        processedGroups.Add(voxel);
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
        ProcessPhysicsRebuilds();
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
                                  $"Queues: Gen={_generationQueue.Count}, Mesh={_meshQueue.Count}, Physics={_physicsRebuildQueue.Count}");
            }
            _memoryLogTimer = 0f;
        }
    }

    private void UpdateVisibleChunks()
    {
        var playerPosition = PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle).Pose.Position;
        var playerChunkPos = new Vector3i(
            (int)Math.Floor(playerPosition.X / Chunk.ChunkSize),
            0,
            (int)Math.Floor(playerPosition.Z / Chunk.ChunkSize)
        );

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

        // Удаляем старые чанки
        var chunksToUnload = new List<Vector3i>();
        lock (_chunksLock)
        {
            foreach (var pos in _activeChunkPositions)
            {
                if (!newRequiredChunks.Contains(pos))
                    chunksToUnload.Add(pos);
            }
        }

        foreach (var pos in chunksToUnload)
        {
            UnloadChunk(pos);
        }

        // Загружаем новые чанки с приоритетом по расстоянию
        var chunksToLoad = newRequiredChunks
            .Where(pos => !_activeChunkPositions.Contains(pos) && !_chunksInProgress.Contains(pos))
            .OrderBy(pos => (pos - playerChunkPos).LengthSquared())
            .ToList();

        foreach (var pos in chunksToLoad)
        {
            _chunksInProgress.Add(pos);
            int priority = (pos - playerChunkPos).LengthSquared();
            _generationQueue.Add(new ChunkGenerationTask { Position = pos, Priority = priority });
        }

        _activeChunkPositions.Clear();
        foreach (var pos in newRequiredChunks)
            _activeChunkPositions.Add(pos);
    }

    private void ProcessGeneratedChunks()
    {
        int processed = 0;
        while (processed < MaxChunksPerFrame && _generatedChunksQueue.TryDequeue(out var result))
        {
            processed++;
            _chunksInProgress.Remove(result.Position);

            if (!_activeChunkPositions.Contains(result.Position))
                continue;

            lock (_chunksLock)
            {
                if (_chunks.ContainsKey(result.Position))
                    continue;

                var chunk = new Chunk(result.Position, this);
                chunk.SetVoxelData(result.Voxels);
                _chunks[result.Position] = chunk;

                int priority = (result.Position - _lastPlayerChunkPosition).LengthSquared();

                // КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: Создаём физику СРАЗУ, не ждём меш!
                QueuePhysicsRebuild(chunk, priority);

                // Меш создаём параллельно (он нужен только для визуала)
                _meshQueue.Add(new MeshGenerationTask
                {
                    ChunkPosition = result.Position,
                    Voxels = new Dictionary<Vector3i, MaterialType>(result.Voxels),
                    Priority = priority,
                    IsUpdate = false
                });
            }
        }
    }

    private void ProcessFinalizedMeshes()
    {
        int processed = 0;

        // КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: Сначала обрабатываем обновления (IsUpdate=true)
        var updates = new List<FinalizedMeshData>();
        var newMeshes = new List<FinalizedMeshData>();

        // Разделяем на обновления и новые меши
        while (_finalizedMeshQueue.TryDequeue(out var data))
        {
            if (data.IsUpdate)
                updates.Add(data);
            else
                newMeshes.Add(data);
        }

        // СНАЧАЛА обрабатываем обновления (разрушение блоков)
        foreach (var data in updates)
        {
            if (processed >= MaxMeshUpdatesPerFrame) break;

            lock (_chunksLock)
            {
                if (_chunks.TryGetValue(data.ChunkPosition, out var chunk))
                {
                    chunk.ApplyMesh(data.Vertices, data.Colors, data.AoValues);
                    processed++;
                }
            }
        }

        // ПОТОМ новые меши
        foreach (var data in newMeshes)
        {
            if (processed >= MaxMeshUpdatesPerFrame) break;

            lock (_chunksLock)
            {
                if (_chunks.TryGetValue(data.ChunkPosition, out var chunk))
                {
                    chunk.ApplyMesh(data.Vertices, data.Colors, data.AoValues);
                    processed++;
                }
            }
        }
    }

    private void ProcessPhysicsRebuilds()
    {
        int processed = 0;
        lock (_physicsRebuildQueue)
        {
            while (processed < MaxPhysicsRebuildsPerFrame && _physicsRebuildQueue.Count > 0)
            {
                var task = _physicsRebuildQueue.Min;
                _physicsRebuildQueue.Remove(task);

                if (task.Chunk != null && task.Chunk.IsLoaded)
                {
                    task.Chunk.RebuildPhysics();
                    processed++;
                }
            }
        }
    }

    private void ProcessDetachmentResults()
    {
        while (_detachmentResultQueue.TryDequeue(out var result))
        {
            if (result.OriginChunk == null || !result.OriginChunk.IsLoaded)
                continue;

            // Удаляем воксели из исходного чанка
            bool removedAny = false;
            lock (result.OriginChunk.VoxelsLock)
            {
                foreach (var voxel in result.DetachedGroup)
                {
                    if (result.OriginChunk.Voxels.Remove(voxel))
                        removedAny = true;
                }
            }

            if (removedAny)
            {
                Console.WriteLine($"[WorldManager] Detached {result.DetachedGroup.Count} voxels, creating dynamic object...");

                // Обновляем меш и физику чанка
                RebuildChunkMeshAsync(result.OriginChunk, priority: 0);
                QueuePhysicsRebuild(result.OriginChunk, priority: 0);

                // КЛЮЧЕВОЕ ИСПРАВЛЕНИЕ: Создаём динамический объект
                // Вычисляем правильную мировую позицию для объекта
                var centerSum = System.Numerics.Vector3.Zero;
                foreach (var voxel in result.DetachedGroup)
                {
                    centerSum += new System.Numerics.Vector3(voxel.X, voxel.Y, voxel.Z);
                }
                centerSum /= result.DetachedGroup.Count;

                var worldPosition = result.WorldOffset + centerSum;

                CreateDetachedVoxelObject(result.DetachedGroup, result.Material, worldPosition);
            }
        }
    }

    private void ProcessVoxelObjects()
    {
        while (_voxelObjectsToAdd.TryDequeue(out var vo))
            _voxelObjects.Add(vo);

        foreach (var vo in _voxelObjects)
        {
            if (PhysicsWorld.Simulation.Bodies.BodyExists(vo.BodyHandle))
            {
                var pose = PhysicsWorld.GetPose(vo.BodyHandle);
                vo.UpdatePose(pose);
            }
        }
    }

    #endregion

    #region Public API

    public void QueuePhysicsRebuild(Chunk chunk, int priority)
    {
        if (chunk == null || !chunk.IsLoaded) return;

        lock (_physicsRebuildQueue)
        {
            _physicsRebuildQueue.Add(new PhysicsRebuildTask { Chunk = chunk, Priority = priority });
        }
    }

    public void RebuildChunkMeshAsync(Chunk chunk, int priority)
    {
        if (chunk == null || !chunk.IsLoaded) return;

        Dictionary<Vector3i, MaterialType> voxelsCopy;
        lock (chunk.VoxelsLock)
        {
            voxelsCopy = new Dictionary<Vector3i, MaterialType>(chunk.Voxels);
        }

        _meshQueue.Add(new MeshGenerationTask
        {
            ChunkPosition = chunk.Position,
            Voxels = voxelsCopy,
            Priority = priority,
            IsUpdate = true
        });
    }

    public void QueueDetachmentCheck(Chunk chunk, Vector3i removedVoxelLocalPos)
    {
        _detachmentQueue.Add(new DetachmentCheckTask
        {
            Chunk = chunk,
            RemovedVoxelLocalPos = removedVoxelLocalPos
        });
    }

    public bool IsVoxelSolidWorld(Vector3i worldPos)
    {
        var chunkPos = new Vector3i(
            (int)Math.Floor(worldPos.X / (float)Chunk.ChunkSize),
            0,
            (int)Math.Floor(worldPos.Z / (float)Chunk.ChunkSize)
        );

        lock (_chunksLock)
        {
            if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded)
            {
                var localPos = new Vector3i(
                    worldPos.X - chunk.Position.X * Chunk.ChunkSize,
                    worldPos.Y,
                    worldPos.Z - chunk.Position.Z * Chunk.ChunkSize
                );

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
        }
        else if (_staticToChunkMap.TryGetValue(collidable.StaticHandle, out var chunk))
        {
            var chunkWorldPos = (chunk.Position * Chunk.ChunkSize).ToSystemNumerics();
            var localHit = worldHitLocation - chunkWorldPos - worldHitNormal * 0.001f;
            var voxelToRemove = new Vector3i(
                (int)Math.Floor(localHit.X + 0.5f),
                (int)Math.Floor(localHit.Y + 0.5f),
                (int)Math.Floor(localHit.Z + 0.5f)
            );

            bool removed;
            lock (chunk.VoxelsLock)
            {
                removed = chunk.Voxels.ContainsKey(voxelToRemove);
            }

            if (removed)
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
                    RebuildChunkMeshAsync(neighbor, priority: 0);
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
                lock (_physicsRebuildQueue)
                {
                    _physicsRebuildQueue.RemoveWhere(t => t.Chunk == chunk);
                }

                chunk.Dispose();
                _chunks.Remove(position);
            }
        }
        _chunksInProgress.Remove(position);
    }

    private void CreateDetachedVoxelObject(List<Vector3i> localCoords, MaterialType material, BepuVector3 chunkWorldPos)
    {
        if (localCoords == null || localCoords.Count == 0) return;

        var newObject = new VoxelObject(localCoords, material, this);
        var handle = PhysicsWorld.CreateVoxelObjectBody(localCoords, material, chunkWorldPos, out var centerOfMass);

        if (!PhysicsWorld.Simulation.Bodies.BodyExists(handle)) return;

        newObject.InitializePhysics(handle, centerOfMass.ToOpenTK());
        newObject.BuildMesh();

        _voxelObjectsToAdd.Enqueue(newObject);
        RegisterVoxelObjectBody(handle, newObject);
    }

    private bool IsConnectedToGround(Chunk startChunk, Vector3i startVoxel, Dictionary<Vector3i, MaterialType> voxels)
    {
        var visited = new HashSet<Vector3i>();
        var queue = new Queue<Vector3i>();

        queue.Enqueue(startVoxel);
        visited.Add(startVoxel);

        var directions = new[]
        {
            new Vector3i(1, 0, 0), new Vector3i(-1, 0, 0),
            new Vector3i(0, 1, 0), new Vector3i(0, -1, 0),
            new Vector3i(0, 0, 1), new Vector3i(0, 0, -1)
        };

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Y == 0) return true; // Достигли земли

            foreach (var dir in directions)
            {
                var neighbor = current + dir;

                if (visited.Contains(neighbor))
                    continue;

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

        var directions = new[]
        {
            new Vector3i(1, 0, 0), new Vector3i(-1, 0, 0),
            new Vector3i(0, 1, 0), new Vector3i(0, -1, 0),
            new Vector3i(0, 0, 1), new Vector3i(0, 0, -1)
        };

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
        var pose = PhysicsWorld.GetPose(targetObject.BodyHandle);
        var invOrientation = System.Numerics.Quaternion.Inverse(pose.Orientation);
        var localHit = BepuVector3.Transform(worldHitLocation - pose.Position, invOrientation) +
                       targetObject.LocalCenterOfMass.ToSystemNumerics() -
                       BepuVector3.Transform(worldHitNormal, invOrientation) * 0.001f;

        var voxelToRemove = new Vector3i(
            (int)Math.Floor(localHit.X + 0.5f),
            (int)Math.Floor(localHit.Y + 0.5f),
            (int)Math.Floor(localHit.Z + 0.5f)
        );

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
                foreach (var voxel in island)
                    localIslandCenter += new BepuVector3(voxel.X, voxel.Y, voxel.Z);
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

        var directions = new[]
        {
            new Vector3i(1, 0, 0), new Vector3i(-1, 0, 0),
            new Vector3i(0, 1, 0), new Vector3i(0, -1, 0),
            new Vector3i(0, 0, 1), new Vector3i(0, 0, -1)
        };

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

        // Останавливаем очереди
        _generationQueue.CompleteAdding();
        _meshQueue.CompleteAdding();
        _detachmentQueue.CompleteAdding();

        // Ждём завершения потоков
        _generationThread?.Join(2000);
        _meshBuilderThread?.Join(2000);
        _detachmentThread?.Join(2000);

        // Очищаем все объекты
        var allObjects = new List<VoxelObject>(_voxelObjects);
        foreach (var vo in allObjects)
        {
            QueueForRemoval(vo);
        }
        ProcessRemovals();

        // Очищаем чанки
        lock (_chunksLock)
        {
            foreach (var chunk in _chunks.Values)
            {
                chunk.Dispose();
            }
            _chunks.Clear();
        }

        _bodyToVoxelObjectMap.Clear();
        _staticToChunkMap.Clear();

        // Освобождаем очереди
        _generationQueue.Dispose();
        _meshQueue.Dispose();
        _detachmentQueue.Dispose();

        Console.WriteLine("[WorldManager] Disposed.");
    }

    public Chunk GetChunk(Vector3i chunkPosition)
    {
        lock (_chunksLock)
        {
            return _chunks.TryGetValue(chunkPosition, out var chunk) ? chunk : null;
        }
    }

    public void RegisterChunkStatic(IEnumerable<StaticHandle> handles, Chunk chunk)
    {
        if (handles == null) return;
        foreach (var h in handles)
            _staticToChunkMap[h] = chunk;
    }

    public void RegisterChunkStatic(StaticHandle handle, Chunk chunk)
    {
        _staticToChunkMap[handle] = chunk;
    }
    public void UnregisterChunkStatic(StaticHandle handle)
    {
        _staticToChunkMap.Remove(handle);
    }

    private void RegisterVoxelObjectBody(BodyHandle handle, VoxelObject voxelObject) => _bodyToVoxelObjectMap[handle] = voxelObject;

    public void QueueForRemoval(VoxelObject obj)
    {
        if (obj != null && !_objectsToRemove.Contains(obj))
            _objectsToRemove.Add(obj);
    }

    private void ProcessRemovals()
    {
        if (_objectsToRemove.Count == 0) return;

        foreach (var obj in _objectsToRemove)
        {
            if (obj == null) continue;

            _bodyToVoxelObjectMap.Remove(obj.BodyHandle);
            PhysicsWorld.RemoveBody(obj.BodyHandle);
            _voxelObjects.Remove(obj);
            obj.Dispose();
        }
        _objectsToRemove.Clear();
    }

    #endregion
}