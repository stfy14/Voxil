// /World/WorldManager.cs
using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;
using BepuVector3 = System.Numerics.Vector3;

public class WorldManager : IDisposable
{
    public PhysicsWorld PhysicsWorld { get; }
    private readonly Dictionary<Vector3i, Chunk> _chunks = new();

    // --- ИЗМЕНЕНИЕ АРХИТЕКТУРЫ ---
    // Вместо одного общего словаря используем два строго типизированных.
    // Это устраняет источник ошибки и делает код чище и безопаснее.
    private readonly Dictionary<BodyHandle, VoxelObject> _bodyToVoxelObjectMap = new();
    private readonly Dictionary<StaticHandle, Chunk> _staticToChunkMap = new();
    // -----------------------------

    private readonly IWorldGenerator _generator;
    private readonly List<VoxelObject> _objectsToRemove = new();

    public WorldManager(PhysicsWorld physicsWorld)
    {
        PhysicsWorld = physicsWorld;
        _generator = new PerlinGenerator(12345);

        var initialChunk = new Chunk(Vector3i.Zero, this);
        _chunks.Add(Vector3i.Zero, initialChunk);
        initialChunk.Generate(_generator);
    }

    // ИЗМЕНЕНО: Регистрация статического чанка
    public void RegisterChunkStatic(StaticHandle handle, Chunk chunk)
    {
        _staticToChunkMap[handle] = chunk;
    }

    // ИЗМЕНЕНО: Регистрация динамического объекта
    private void RegisterVoxelObjectBody(BodyHandle handle, VoxelObject voxelObject)
    {
        _bodyToVoxelObjectMap[handle] = voxelObject;
    }

    public void QueueForRemoval(VoxelObject obj)
    {
        if (obj != null && !_objectsToRemove.Contains(obj))
            _objectsToRemove.Add(obj);
    }

    // КЛЮЧЕВОЕ ИЗМЕНЕНИЕ: Логика разрушения
    public void DestroyVoxelAt(CollidableReference collidable, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        // В зависимости от типа коллайдера, ищем в соответствующем словаре.
        if (collidable.Mobility == CollidableMobility.Dynamic)
        {
            // Это динамический объект
            if (_bodyToVoxelObjectMap.TryGetValue(collidable.BodyHandle, out var dynamicObject))
            {
                DestroyDynamicVoxelAt(dynamicObject, worldHitLocation, worldHitNormal);
            }
        }
        else // Static or Kinematic
        {
            // Это статический чанк
            if (_staticToChunkMap.TryGetValue(collidable.StaticHandle, out var chunk))
            {
                DestroyStaticVoxelAt(chunk, worldHitLocation, worldHitNormal);
            }
        }
    }

    // НОВЫЙ МЕТОД: Логика разрушения вокселя в чанке вынесена для чистоты
    private void DestroyStaticVoxelAt(Chunk chunk, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        var chunkWorldPos = (chunk.Position * Chunk.ChunkSize).ToSystemNumerics();
        var localHitLocation = worldHitLocation - chunkWorldPos - worldHitNormal * 0.001f;

        var voxelToRemove = new Vector3i(
            (int)Math.Floor(localHitLocation.X + 0.5f),
            (int)Math.Floor(localHitLocation.Y + 0.5f),
            (int)Math.Floor(localHitLocation.Z + 0.5f));

        if (chunk.RemoveVoxelAt(voxelToRemove))
        {
            var voxelList = new List<Vector3i> { new(0, 0, 0) };
            var voxelWorldPosition = chunkWorldPos + new BepuVector3(voxelToRemove.X, voxelToRemove.Y, voxelToRemove.Z);
            _createAndAddVoxelObject(voxelList, MaterialType.Stone, voxelWorldPosition);
        }
    }


    public void Update(float deltaTime)
    {
        ProcessRemovals();
        foreach (var chunk in _chunks.Values)
            chunk.Update(deltaTime); // Передаем deltaTime
    }

    public void Render(Shader shader, Matrix4 view, Matrix4 projection)
    {
        foreach (var chunk in _chunks.Values)
            chunk.Render(shader, view, projection);
    }

    public void Dispose()
    {
        // Используем значения из словаря для удаления, это надежнее
        foreach (var vo in _bodyToVoxelObjectMap.Values)
            QueueForRemoval(vo);

        ProcessRemovals();

        foreach (var chunk in _chunks.Values)
            chunk.Dispose();

        _chunks.Clear();
        _bodyToVoxelObjectMap.Clear();
        _staticToChunkMap.Clear();
    }

    // ИЗМЕНЕНО: Процесс удаления
    private void ProcessRemovals()
    {
        if (_objectsToRemove.Count == 0) return;

        foreach (var obj in _objectsToRemove)
        {
            if (obj == null) continue;

            // Удаляем из словаря по ключу BodyHandle. Просто и надежно.
            _bodyToVoxelObjectMap.Remove(obj.BodyHandle);

            PhysicsWorld.RemoveBody(obj.BodyHandle);
            // TODO: Это может быть не всегда Zero, нужно будет улучшить в будущем
            _chunks[Vector3i.Zero].RemoveVoxelObject(obj);
            obj.Dispose();
        }
        _objectsToRemove.Clear();
    }

    private VoxelObject _createAndAddVoxelObject(List<Vector3i> localVoxelCoordinates, MaterialType material, BepuVector3 worldPosition)
    {
        if (localVoxelCoordinates == null || localVoxelCoordinates.Count == 0) return null;

        var newObject = new VoxelObject(localVoxelCoordinates, material);
        var handle = PhysicsWorld.CreateVoxelObjectBody(localVoxelCoordinates, material, worldPosition, out var newCenterOfMass);

        if (!PhysicsWorld.Simulation.Bodies.BodyExists(handle)) return null;

        newObject.InitializePhysics(handle, newCenterOfMass.ToOpenTK());
        newObject.BuildMesh();

        // TODO: Логика добавления в правильный чанк
        _chunks[Vector3i.Zero].AddVoxelObject(newObject);
        // Регистрируем в новом, правильном словаре
        RegisterVoxelObjectBody(handle, newObject);

        return newObject;
    }

    // Этот метод остается практически без изменений
    private void DestroyDynamicVoxelAt(VoxelObject targetObject, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        var pose = PhysicsWorld.GetPose(targetObject.BodyHandle);
        var invOrientation = System.Numerics.Quaternion.Inverse(pose.Orientation);
        var localHitLocation = BepuVector3.Transform(worldHitLocation - pose.Position, invOrientation) + targetObject.LocalCenterOfMass.ToSystemNumerics();
        var localNormal = BepuVector3.Transform(worldHitNormal, invOrientation);
        localHitLocation -= localNormal * 0.001f;

        var voxelToRemove = new Vector3i(
            (int)Math.Floor(localHitLocation.X + 0.5f),
            (int)Math.Floor(localHitLocation.Y + 0.5f),
            (int)Math.Floor(localHitLocation.Z + 0.5f));

        if (!targetObject.VoxelCoordinates.Contains(voxelToRemove)) return;

        var remainingVoxels = new List<Vector3i>(targetObject.VoxelCoordinates);
        remainingVoxels.Remove(voxelToRemove);

        if (remainingVoxels.Count == 0)
        {
            QueueForRemoval(targetObject);
            return;
        }

        List<List<Vector3i>> newVoxelIslands = FindConnectedVoxelIslands(remainingVoxels);

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
                foreach (var voxel in island)
                    localIslandCenter += new BepuVector3(voxel.X, voxel.Y, voxel.Z);
                localIslandCenter /= island.Count;

                var offsetFromOldCoM = localIslandCenter - targetObject.LocalCenterOfMass.ToSystemNumerics();
                var rotatedOffset = BepuVector3.Transform(offsetFromOldCoM, originalPose.Orientation);
                var newWorldPosition = originalPose.Position + rotatedOffset;

                _createAndAddVoxelObject(island, originalMaterial, newWorldPosition);
            }

            QueueForRemoval(targetObject);
        }
    }

    // Этот метод остается без изменений
    private List<List<Vector3i>> FindConnectedVoxelIslands(List<Vector3i> voxels)
    {
        var islands = new List<List<Vector3i>>();
        var voxelsToVisit = new HashSet<Vector3i>(voxels);

        while (voxelsToVisit.Count > 0)
        {
            var newIsland = new List<Vector3i>();
            var queue = new Queue<Vector3i>();
            queue.Enqueue(voxelsToVisit.First());
            voxelsToVisit.Remove(queue.Peek());

            while (queue.Count > 0)
            {
                var currentVoxel = queue.Dequeue();
                newIsland.Add(currentVoxel);

                var neighbors = new Vector3i[]
                {
                    currentVoxel + new Vector3i(1,0,0), currentVoxel + new Vector3i(-1,0,0),
                    currentVoxel + new Vector3i(0,1,0), currentVoxel + new Vector3i(0,-1,0),
                    currentVoxel + new Vector3i(0,0,1), currentVoxel + new Vector3i(0,0,-1)
                };

                foreach (var neighbor in neighbors)
                {
                    if (voxelsToVisit.Contains(neighbor))
                    {
                        voxelsToVisit.Remove(neighbor);
                        queue.Enqueue(neighbor);
                    }
                }
            }
            islands.Add(newIsland);
        }

        return islands;
    }
}