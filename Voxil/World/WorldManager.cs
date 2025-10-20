// /World/WorldManager.cs
using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BepuVector3 = System.Numerics.Vector3;

#region Helper Classes for Async Tasks

/// <summary>
/// Результат работы потока генерации: содержит только данные о вокселях.
/// </summary>
public class ChunkGenerationResult
{
    public Vector3i Position { get; }
    public Dictionary<Vector3i, MaterialType> Voxels { get; }

    public ChunkGenerationResult(Vector3i position, Dictionary<Vector3i, MaterialType> voxels)
    {
        Position = position;
        Voxels = voxels;
    }
}

/// <summary>
/// Задача для потока финализации: содержит данные для построения финального меша.
/// </summary>
public class ChunkFinalizeRequest
{
    public Vector3i Position;
    public Dictionary<Vector3i, MaterialType> Voxels;
    public Func<Vector3i, bool> IsVoxelSolidGlobalFunc;
}

/// <summary>
/// Результат работы потока финализации: содержит готовый меш.
/// </summary>
public class FinalizedChunkData
{
    public Vector3i Position;
    public List<float> Vertices;
    public List<float> Colors;
    public List<float> AoValues;
}

/// <summary>
/// Задача для потока проверки отсоединения.
/// </summary>
public class DetachmentCheckRequest
{
    public Chunk Chunk;
    public Vector3i RemovedVoxelLocalPos;
}

/// <summary>
/// Результат работы потока проверки отсоединения.
/// </summary>
public class DetachmentCheckResult
{
    public Chunk OriginChunk;
    public List<Vector3i> DetachedGroup;
    public MaterialType Material;
}

#endregion

public class WorldManager : IDisposable
{
    public PhysicsWorld PhysicsWorld { get; }
    private readonly PlayerController _playerController;

    private int _viewDistance = 16;
    private readonly Dictionary<Vector3i, Chunk> _chunks = new();
    private readonly Dictionary<BodyHandle, VoxelObject> _bodyToVoxelObjectMap = new();
    private readonly Dictionary<StaticHandle, Chunk> _staticToChunkMap = new();
    private readonly IWorldGenerator _generator;

    private readonly List<VoxelObject> _voxelObjects = new List<VoxelObject>();
    private readonly ConcurrentQueue<VoxelObject> _voxelObjectsToAdd = new ConcurrentQueue<VoxelObject>();
    private readonly List<VoxelObject> _objectsToRemove = new List<VoxelObject>();
    private float _memoryLogTimer = 0f;
    private bool _isDisposed = false;

    // --- Threads ---
    private readonly Thread _generationThread;
    private readonly Thread _finalizationThread;
    private readonly Thread _detachmentCheckThread;

    // --- Queues ---
    private readonly ConcurrentQueue<Vector3i> _generationQueue = new ConcurrentQueue<Vector3i>();
    private readonly ConcurrentQueue<ChunkGenerationResult> _initialDataQueue = new ConcurrentQueue<ChunkGenerationResult>();

    private readonly ConcurrentQueue<ChunkFinalizeRequest> _finalizeQueue = new ConcurrentQueue<ChunkFinalizeRequest>();
    private readonly ConcurrentQueue<FinalizedChunkData> _finalizedDataQueue = new ConcurrentQueue<FinalizedChunkData>();

    private readonly ConcurrentQueue<DetachmentCheckRequest> _detachmentCheckQueue = new ConcurrentQueue<DetachmentCheckRequest>();
    private readonly ConcurrentQueue<DetachmentCheckResult> _detachmentResultQueue = new ConcurrentQueue<DetachmentCheckResult>();

    private readonly HashSet<Chunk> _chunksNeedingPhysicsRebuild = new HashSet<Chunk>();
    private readonly HashSet<Vector3i> _chunksInProgress = new HashSet<Vector3i>();
    private Vector3i _lastPlayerChunkPosition = new Vector3i(int.MaxValue);
    private readonly HashSet<Vector3i> _activeChunkPositions = new HashSet<Vector3i>();
    private const int ChunksToProcessPerFrame = 8;

    public WorldManager(PhysicsWorld physicsWorld, PlayerController playerController)
    {
        PhysicsWorld = physicsWorld;
        _playerController = playerController;
        _generator = new PerlinGenerator(12345);

        _isDisposed = false;
        _generationThread = new Thread(GenerationLoop) { IsBackground = true, Name = "GenerationThread" };
        _generationThread.Start();

        _finalizationThread = new Thread(FinalizationLoop) { IsBackground = true, Name = "FinalizationThread" };
        _finalizationThread.Start();

        _detachmentCheckThread = new Thread(DetachmentCheckLoop) { IsBackground = true, Name = "DetachmentCheckThread" };
        _detachmentCheckThread.Start();
    }

    #region Thread Loops

    private void GenerationLoop()
    {
        Console.WriteLine("[GenerationThread] Рабочий поток запущен.");
        while (!_isDisposed)
        {
            if (_generationQueue.TryDequeue(out var chunkPos))
            {
                var voxels = new Dictionary<Vector3i, MaterialType>();
                _generator.GenerateChunk(chunkPos, voxels);
                var result = new ChunkGenerationResult(chunkPos, voxels);
                _initialDataQueue.Enqueue(result);
            }
            else
            {
                Thread.Sleep(15);
            }
        }
        Console.WriteLine("[GenerationThread] Рабочий поток остановлен.");
    }

    private void FinalizationLoop()
    {
        Console.WriteLine("[FinalizationThread] Рабочий поток запущен.");
        while (!_isDisposed)
        {
            if (_finalizeQueue.TryDequeue(out var request))
            {
                VoxelMeshBuilder.GenerateMesh(request.Voxels,
                    out var vertices, out var colors, out var aoValues,
                    request.IsVoxelSolidGlobalFunc);

                var result = new FinalizedChunkData
                {
                    Position = request.Position,
                    Vertices = vertices,
                    Colors = colors,
                    AoValues = aoValues
                };
                _finalizedDataQueue.Enqueue(result);
            }
            else
            {
                Thread.Sleep(10);
            }
        }
        Console.WriteLine("[FinalizationThread] Рабочий поток остановлен.");
    }

    private void DetachmentCheckLoop()
    {
        Console.WriteLine("[DetachmentThread] Рабочий поток запущен.");
        while (!_isDisposed)
        {
            if (_detachmentCheckQueue.TryDequeue(out var request))
            {
                var neighbors = new List<Vector3i>
                {
                    request.RemovedVoxelLocalPos + new Vector3i(1, 0, 0), request.RemovedVoxelLocalPos + new Vector3i(-1, 0, 0),
                    request.RemovedVoxelLocalPos + new Vector3i(0, 1, 0), request.RemovedVoxelLocalPos + new Vector3i(0, -1, 0),
                    request.RemovedVoxelLocalPos + new Vector3i(0, 0, 1), request.RemovedVoxelLocalPos + new Vector3i(0, 0, -1)
                };

                var processedGroups = new HashSet<Vector3i>();
                foreach (var neighborPos in neighbors)
                {
                    if (!request.Chunk.Voxels.ContainsKey(neighborPos) || processedGroups.Contains(neighborPos)) continue;

                    if (!IsConnectedToGround(request.Chunk, neighborPos))
                    {
                        var detachedGroup = GetConnectedVoxels(request.Chunk, neighborPos);
                        if (detachedGroup.Count > 0 && request.Chunk.Voxels.TryGetValue(detachedGroup[0], out var mat))
                        {
                            _detachmentResultQueue.Enqueue(new DetachmentCheckResult
                            {
                                OriginChunk = request.Chunk,
                                DetachedGroup = detachedGroup,
                                Material = mat
                            });

                            foreach (var voxel in detachedGroup)
                            {
                                processedGroups.Add(voxel);
                            }
                        }
                    }
                }
            }
            else
            {
                Thread.Sleep(20);
            }
        }
        Console.WriteLine("[DetachmentThread] Рабочий поток остановлен.");
    }

    #endregion

    public void Update(float deltaTime)
    {
        UpdateVisibleChunkList();
        ProcessInitialDataQueue();
        ProcessFinalizedDataQueue();
        ProcessDetachmentResults();
        ProcessPhysicsRebuildQueue();

        while (_voxelObjectsToAdd.TryDequeue(out var vo)) _voxelObjects.Add(vo);

        foreach (var vo in _voxelObjects)
        {
            if (PhysicsWorld.Simulation.Bodies.BodyExists(vo.BodyHandle))
            {
                var pose = PhysicsWorld.GetPose(vo.BodyHandle);
                vo.UpdatePose(pose);
            }
        }

        ProcessRemovals();

        _memoryLogTimer += deltaTime;
        if (_memoryLogTimer >= 5.0f)
        {
            var loadedCount = _chunks.Values.Count(c => c._isLoaded);
            var memoryMB = GC.GetTotalMemory(false) / (1024 * 1024);
            Console.WriteLine($"[World] Chunks: {loadedCount}/{_chunks.Count}, VObjects: {_voxelObjects.Count}, Mem: {memoryMB}MB");
            _memoryLogTimer = 0f;
        }
    }

    #region Queue Processing

    private void ProcessInitialDataQueue()
    {
        for (int i = 0; i < ChunksToProcessPerFrame && _initialDataQueue.TryDequeue(out var result); i++)
        {
            if (!_activeChunkPositions.Contains(result.Position))
            {
                _chunksInProgress.Remove(result.Position);
                continue;
            }

            if (!_chunks.ContainsKey(result.Position))
            {
                var newChunk = new Chunk(result.Position, this);
                newChunk.SetVoxelData(result.Voxels);
                _chunks.Add(result.Position, newChunk);

                Func<Vector3i, bool> solidCheckFunc = localPos => IsVoxelSolidWorld((newChunk.Position * Chunk.ChunkSize) + localPos);
                QueueForFinalization(new ChunkFinalizeRequest
                {
                    Position = newChunk.Position,
                    Voxels = newChunk.Voxels,
                    IsVoxelSolidGlobalFunc = solidCheckFunc
                });
            }
            _chunksInProgress.Remove(result.Position);
        }
    }

    private void ProcessFinalizedDataQueue()
    {
        while (_finalizedDataQueue.TryDequeue(out var data))
        {
            if (_chunks.TryGetValue(data.Position, out var chunk))
            {
                chunk.ApplyFinalizedData(data);
            }
        }
    }

    private void ProcessDetachmentResults()
    {
        while (_detachmentResultQueue.TryDequeue(out var result))
        {
            if (result.OriginChunk == null || !result.OriginChunk._isLoaded) continue;

            bool removedAny = false;
            foreach (var voxel in result.DetachedGroup)
            {
                if (result.OriginChunk.Voxels.Remove(voxel))
                {
                    removedAny = true;
                }
            }

            if (removedAny)
            {
                result.OriginChunk.RebuildMesh();
            }

            var chunkWorldPos = (result.OriginChunk.Position * Chunk.ChunkSize).ToSystemNumerics();
            CreateAndAddVoxelObject(result.DetachedGroup, result.Material, chunkWorldPos);
        }
    }

    private void ProcessPhysicsRebuildQueue()
    {
        var chunksToProcess = _chunksNeedingPhysicsRebuild.Take(4).ToList();
        foreach (var chunk in chunksToProcess)
        {
            if (chunk != null && chunk._isLoaded)
            {
                chunk.RebuildPhysics();
            }
            _chunksNeedingPhysicsRebuild.Remove(chunk);
        }
    }

    #endregion

    #region Public API and Helpers

    public void QueueForFinalization(ChunkFinalizeRequest request)
    {
        _finalizeQueue.Enqueue(request);
    }

    public void QueueForPhysicsRebuild(Chunk chunk)
    {
        if (chunk != null && chunk._isLoaded)
        {
            _chunksNeedingPhysicsRebuild.Add(chunk);
        }
    }

    public void QueueForDetachmentCheck(Chunk chunk, Vector3i removedVoxelLocalPos)
    {
        _detachmentCheckQueue.Enqueue(new DetachmentCheckRequest
        {
            Chunk = chunk,
            RemovedVoxelLocalPos = removedVoxelLocalPos
        });
    }

    public void NotifyNeighborsOfVoxelChange(Vector3i chunkPos, Vector3i localVoxelPos)
    {
        Action<Vector3i> rebuildNeighbor = (offset) =>
        {
            if (_chunks.TryGetValue(chunkPos + offset, out var neighbor))
            {
                neighbor.RebuildMesh();
            }
        };

        if (localVoxelPos.X == 0) rebuildNeighbor(new Vector3i(-1, 0, 0));
        else if (localVoxelPos.X == Chunk.ChunkSize - 1) rebuildNeighbor(new Vector3i(1, 0, 0));
        if (localVoxelPos.Z == 0) rebuildNeighbor(new Vector3i(0, 0, -1));
        else if (localVoxelPos.Z == Chunk.ChunkSize - 1) rebuildNeighbor(new Vector3i(0, 0, 1));
    }

    public bool IsVoxelSolidWorld(Vector3i worldPos)
    {
        var chunkPos = new Vector3i(
            (int)Math.Floor(worldPos.X / (float)Chunk.ChunkSize), 0,
            (int)Math.Floor(worldPos.Z / (float)Chunk.ChunkSize));

        if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk._isLoaded)
        {
            var localPos = new Vector3i(
                worldPos.X - chunk.Position.X * Chunk.ChunkSize,
                worldPos.Y,
                worldPos.Z - chunk.Position.Z * Chunk.ChunkSize
            );
            return chunk.Voxels.ContainsKey(localPos);
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
            var voxelToRemove = new Vector3i((int)Math.Floor(localHit.X + 0.5f), (int)Math.Floor(localHit.Y + 0.5f), (int)Math.Floor(localHit.Z + 0.5f));

            if (chunk.Voxels.ContainsKey(voxelToRemove))
            {
                chunk.RemoveVoxelAndUpdate(voxelToRemove);
            }
        }
    }

    #endregion

    #region Private Logic

    private void UpdateVisibleChunkList()
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

        var previousActiveChunks = new HashSet<Vector3i>(_activeChunkPositions);
        previousActiveChunks.ExceptWith(newRequiredChunks);

        foreach (var posToUnload in previousActiveChunks)
        {
            if (_chunks.TryGetValue(posToUnload, out var chunk))
            {
                _chunksNeedingPhysicsRebuild.Remove(chunk);
                chunk.Dispose();
                _chunks.Remove(posToUnload);
            }
            _chunksInProgress.Remove(posToUnload);
        }

        var chunksToLoad = new List<Vector3i>();
        foreach (var posToLoad in newRequiredChunks)
        {
            if (!_activeChunkPositions.Contains(posToLoad))
            {
                chunksToLoad.Add(posToLoad);
            }
        }

        chunksToLoad.Sort((a, b) => (a - playerChunkPos).LengthSquared().CompareTo((b - playerChunkPos).LengthSquared()));

        foreach (var pos in chunksToLoad)
        {
            if (!_chunksInProgress.Contains(pos) && !_chunks.ContainsKey(pos))
            {
                _chunksInProgress.Add(pos);
                _generationQueue.Enqueue(pos);
            }
        }

        _activeChunkPositions.Clear();
        foreach (var pos in newRequiredChunks)
        {
            _activeChunkPositions.Add(pos);
        }
    }

    private bool IsConnectedToGround(Chunk startChunk, Vector3i startVoxel)
    {
        var visited = new HashSet<(Vector3i, Vector3i)>();
        var queue = new Queue<(Chunk, Vector3i)>();

        queue.Enqueue((startChunk, startVoxel));
        visited.Add((startChunk.Position, startVoxel));

        while (queue.Count > 0)
        {
            var (currentChunk, currentLocalPos) = queue.Dequeue();
            if (currentLocalPos.Y == 0) return true;

            var directions = new[] { new Vector3i(1, 0, 0), new Vector3i(-1, 0, 0), new Vector3i(0, 1, 0), new Vector3i(0, -1, 0), new Vector3i(0, 0, 1), new Vector3i(0, 0, -1) };
            foreach (var dir in directions)
            {
                var neighborLocalPos = currentLocalPos + dir;
                var neighborChunk = currentChunk;

                if (neighborLocalPos.X < 0) { neighborChunk = GetChunk(currentChunk.Position + new Vector3i(-1, 0, 0)); if (neighborChunk != null) neighborLocalPos.X = Chunk.ChunkSize - 1; }
                else if (neighborLocalPos.X >= Chunk.ChunkSize) { neighborChunk = GetChunk(currentChunk.Position + new Vector3i(1, 0, 0)); if (neighborChunk != null) neighborLocalPos.X = 0; }
                if (neighborLocalPos.Z < 0) { neighborChunk = GetChunk(currentChunk.Position + new Vector3i(0, 0, -1)); if (neighborChunk != null) neighborLocalPos.Z = Chunk.ChunkSize - 1; }
                else if (neighborLocalPos.Z >= Chunk.ChunkSize) { neighborChunk = GetChunk(currentChunk.Position + new Vector3i(0, 0, 1)); if (neighborChunk != null) neighborLocalPos.Z = 0; }

                if (neighborChunk == null || !neighborChunk._isLoaded) continue;

                if (neighborChunk.Voxels.ContainsKey(neighborLocalPos) && !visited.Contains((neighborChunk.Position, neighborLocalPos)))
                {
                    visited.Add((neighborChunk.Position, neighborLocalPos));
                    queue.Enqueue((neighborChunk, neighborLocalPos));
                }
            }
        }
        return false;
    }

    private List<Vector3i> GetConnectedVoxels(Chunk chunk, Vector3i startVoxel)
    {
        var result = new List<Vector3i>();
        var visited = new HashSet<Vector3i>();
        var queue = new Queue<Vector3i>();
        queue.Enqueue(startVoxel);
        visited.Add(startVoxel);
        result.Add(startVoxel);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var neighbors = new[] { new Vector3i(1, 0, 0), new Vector3i(-1, 0, 0), new Vector3i(0, 1, 0), new Vector3i(0, -1, 0), new Vector3i(0, 0, 1), new Vector3i(0, 0, -1) };
            foreach (var dir in neighbors)
            {
                var neighbor = current + dir;
                if (!visited.Contains(neighbor) && chunk.Voxels.ContainsKey(neighbor))
                {
                    visited.Add(neighbor);
                    queue.Enqueue(neighbor);
                    result.Add(neighbor);
                }
            }
        }
        return result;
    }

    public VoxelObject CreateAndAddVoxelObject(List<Vector3i> localVoxelCoordinates, MaterialType material, BepuVector3 worldPosition)
    {
        return _createAndAddVoxelObject(localVoxelCoordinates, material, worldPosition);
    }

    private VoxelObject _createAndAddVoxelObject(List<Vector3i> localVoxelCoordinates, MaterialType material, BepuVector3 worldPosition)
    {
        if (localVoxelCoordinates == null || localVoxelCoordinates.Count == 0) return null;
        var newObject = new VoxelObject(localVoxelCoordinates, material, this);
        var handle = PhysicsWorld.CreateVoxelObjectBody(localVoxelCoordinates, material, worldPosition, out var newCenterOfMass);
        if (!PhysicsWorld.Simulation.Bodies.BodyExists(handle)) return null;
        newObject.InitializePhysics(handle, newCenterOfMass.ToOpenTK());
        newObject.BuildMesh();
        _voxelObjectsToAdd.Enqueue(newObject);
        RegisterVoxelObjectBody(handle, newObject);
        return newObject;
    }

    private void DestroyDynamicVoxelAt(VoxelObject targetObject, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        var pose = PhysicsWorld.GetPose(targetObject.BodyHandle);
        var invOrientation = System.Numerics.Quaternion.Inverse(pose.Orientation);
        var localHit = BepuVector3.Transform(worldHitLocation - pose.Position, invOrientation) + targetObject.LocalCenterOfMass.ToSystemNumerics() - BepuVector3.Transform(worldHitNormal, invOrientation) * 0.001f;
        var voxelToRemove = new Vector3i((int)Math.Floor(localHit.X + 0.5f), (int)Math.Floor(localHit.Y + 0.5f), (int)Math.Floor(localHit.Z + 0.5f));

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
            targetObject.RebuildMeshAndPhysics(this.PhysicsWorld);
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
                CreateAndAddVoxelObject(island, originalMaterial, newWorldPosition);
            }
            QueueForRemoval(targetObject);
        }
    }

    public List<List<Vector3i>> FindConnectedVoxelIslands(List<Vector3i> voxels)
    {
        var islands = new List<List<Vector3i>>();
        var voxelsToVisit = new HashSet<Vector3i>(voxels);
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
                var neighbors = new[] { new Vector3i(1, 0, 0), new Vector3i(-1, 0, 0), new Vector3i(0, 1, 0), new Vector3i(0, -1, 0), new Vector3i(0, 0, 1), new Vector3i(0, 0, -1) };
                foreach (var dir in neighbors)
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

    #region Boilerplate

    public void Render(Shader shader, Matrix4 view, Matrix4 projection)
    {
        foreach (var chunk in _chunks.Values)
        {
            chunk.Render(shader, view, projection);
        }

        foreach (var vo in _voxelObjects)
        {
            vo.Render(shader, view, projection);
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        _generationThread?.Join();
        _finalizationThread?.Join();
        _detachmentCheckThread?.Join();

        var allObjects = new List<VoxelObject>(_voxelObjects);
        foreach (var vo in allObjects)
        {
            QueueForRemoval(vo);
        }
        ProcessRemovals();

        foreach (var chunk in _chunks.Values)
        {
            chunk.Dispose();
        }
        _chunks.Clear();
        _bodyToVoxelObjectMap.Clear();
        _staticToChunkMap.Clear();
    }

    public Chunk GetChunk(Vector3i chunkPosition) => _chunks.TryGetValue(chunkPosition, out var chunk) ? chunk : null;
    public void RegisterChunkStatic(StaticHandle handle, Chunk chunk) => _staticToChunkMap[handle] = chunk;
    private void RegisterVoxelObjectBody(BodyHandle handle, VoxelObject voxelObject) => _bodyToVoxelObjectMap[handle] = voxelObject;
    public void QueueForRemoval(VoxelObject obj) { if (obj != null && !_objectsToRemove.Contains(obj)) _objectsToRemove.Add(obj); }
    public void UnregisterChunkStatic(StaticHandle handle) => _staticToChunkMap.Remove(handle);

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