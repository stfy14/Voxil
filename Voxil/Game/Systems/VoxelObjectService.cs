using BepuPhysics;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

/// <summary>
/// Отвечает за жизненный цикл динамических воксельных объектов:
/// спавн, обновление поз, расколы, удаление.
/// </summary>
public class VoxelObjectService : IVoxelObjectService
{
    private readonly WorldManager _worldManager;

    private readonly List<VoxelObject> _voxelObjects = new();
    private readonly Dictionary<BodyHandle, VoxelObject> _bodyToVoxelObjectMap = new();
    private readonly ConcurrentQueue<VoxelObjectCreationData> _objectsCreationQueue = new();
    private readonly List<VoxelObject> _objectsToRemove = new();
    private readonly object _objectsLock = new();

    private struct VoxelObjectCreationData
    {
        public List<Vector3i> Voxels;
        public MaterialType BaseMaterial;
        public Dictionary<Vector3i, uint> PerVoxelMaterials;
        public System.Numerics.Vector3 WorldPosition;
    }

    public VoxelObjectService(WorldManager worldManager)
    {
        _worldManager = worldManager;
    }

    // -------------------------------------------------------------------------
    // Update (вызывается из WorldManager.Update)
    // -------------------------------------------------------------------------

    public void Update(Stopwatch mainThreadStopwatch)
    {
        ProcessNewDebris();
        ProcessVoxelObjects();
        ProcessRemovals();
    }

    private void ProcessNewDebris()
    {
        while (_objectsCreationQueue.TryDequeue(out var data))
        {
            var vo = new VoxelObject(data.Voxels, data.BaseMaterial, 1.0f, data.PerVoxelMaterials);
            vo.OnEmpty += QueueForRemoval;

            var handle = _worldManager.PhysicsWorld.CreateVoxelObjectBody(
                data.Voxels,
                data.BaseMaterial,
                1.0f,
                data.WorldPosition,
                out var localCoM);

            if (handle.Value == -1) continue;

            var finalWorldPos = data.WorldPosition + localCoM;
            var bodyRef = _worldManager.PhysicsWorld.Simulation.Bodies.GetBodyReference(handle);
            bodyRef.Pose.Position = finalWorldPos;

            vo.InitializePhysics(handle, localCoM.ToOpenTK());

            lock (_objectsLock)
            {
                _voxelObjects.Add(vo);
                _bodyToVoxelObjectMap[handle] = vo;
            }
        }
    }

    private void ProcessVoxelObjects()
    {
        lock (_objectsLock)
        {
            foreach (var vo in _voxelObjects)
            {
                if (_worldManager.PhysicsWorld.Simulation.Bodies.BodyExists(vo.BodyHandle))
                {
                    var pose = _worldManager.PhysicsWorld.GetPose(vo.BodyHandle);
                    vo.UpdatePose(pose);
                }
            }
        }
    }

    private void ProcessRemovals()
    {
        foreach (var obj in _objectsToRemove)
        {
            try
            {
                lock (_objectsLock)
                {
                    _bodyToVoxelObjectMap.Remove(obj.BodyHandle);
                    _voxelObjects.Remove(obj);
                }
                _worldManager.PhysicsWorld.RemoveBody(obj.BodyHandle);
                obj.Dispose();
            }
            catch { }
        }
        _objectsToRemove.Clear();
    }

    private void QueueForRemoval(VoxelObject obj) => _objectsToRemove.Add(obj);

    // -------------------------------------------------------------------------
    // IVoxelObjectService
    // -------------------------------------------------------------------------

    public List<VoxelObject> GetAllVoxelObjects()
    {
        lock (_objectsLock) return new List<VoxelObject>(_voxelObjects);
    }

    public void SpawnDynamicObject(VoxelObject vo, System.Numerics.Vector3 position, System.Numerics.Vector3 velocity)
    {
        var handle = _worldManager.PhysicsWorld.CreateVoxelObjectBody(
            vo.VoxelCoordinates, vo.Material, vo.Scale, position, out var localCoM);

        if (handle.Value == -1)
        {
            Console.WriteLine($"[Physics] Rejected invalid dynamic object: {vo.Material}");
            return;
        }

        var bodyRef = _worldManager.PhysicsWorld.Simulation.Bodies.GetBodyReference(handle);
        bodyRef.Velocity.Linear = velocity;
        bodyRef.Awake = true;

        vo.InitializePhysics(handle, localCoM.ToOpenTK());

        lock (_objectsLock)
        {
            _voxelObjects.Add(vo);
            _bodyToVoxelObjectMap[handle] = vo;
        }
    }

    public void SpawnComplexObject(System.Numerics.Vector3 position, List<Vector3i> localVoxels, MaterialType material)
    {
        _objectsCreationQueue.Enqueue(new VoxelObjectCreationData
        {
            Voxels = localVoxels,
            BaseMaterial = material,
            PerVoxelMaterials = null,
            WorldPosition = position
        });
    }

    public void SpawnComplexObject(System.Numerics.Vector3 position, List<Vector3i> localVoxels, MaterialType material, Dictionary<Vector3i, uint> perVoxelMaterials)
    {
        _objectsCreationQueue.Enqueue(new VoxelObjectCreationData
        {
            Voxels = localVoxels,
            BaseMaterial = material,
            PerVoxelMaterials = perVoxelMaterials,
            WorldPosition = position
        });
    }

    public void DestroyVoxelObject(VoxelObject vo)
    {
        if (vo == null) return;
        QueueForRemoval(vo);
    }

    public void CreateDetachedObject(List<Vector3i> globalCluster)
    {
        if (globalCluster.Count == 0) return;

        var editService = ServiceLocator.Get<IVoxelEditService>();

        Vector3i minIdx = globalCluster[0];
        foreach (var v in globalCluster)
        {
            if (v.X < minIdx.X) minIdx.X = v.X;
            if (v.Y < minIdx.Y) minIdx.Y = v.Y;
            if (v.Z < minIdx.Z) minIdx.Z = v.Z;
        }

        var localVoxels = new List<Vector3i>();
        var materials = new Dictionary<Vector3i, uint>();
        MaterialType dominantMat = MaterialType.Stone;

        foreach (var v in globalCluster)
        {
            Vector3i localPos = v - minIdx;
            localVoxels.Add(localPos);

            MaterialType mat = editService.GetMaterialGlobal(v);
            if (mat != MaterialType.Air)
            {
                materials[localPos] = (uint)mat;
                dominantMat = mat;
            }
        }

        foreach (var pos in globalCluster)
            editService.RemoveVoxelGlobal(pos);

        localVoxels.Sort((a, b) =>
        {
            if (a.X != b.X) return a.X.CompareTo(b.X);
            if (a.Y != b.Y) return a.Y.CompareTo(b.Y);
            return a.Z.CompareTo(b.Z);
        });

        var worldPos = new System.Numerics.Vector3(
            minIdx.X * Constants.VoxelSize,
            minIdx.Y * Constants.VoxelSize,
            minIdx.Z * Constants.VoxelSize);

        _objectsCreationQueue.Enqueue(new VoxelObjectCreationData
        {
            Voxels = localVoxels,
            BaseMaterial = dominantMat,
            PerVoxelMaterials = materials,
            WorldPosition = worldPos
        });
    }

    public void ProcessDynamicObjectSplits(VoxelObject vo)
    {
        if (vo.VoxelCoordinates.Count == 0) return;

        var clusters = vo.GetConnectedClusters();
        if (clusters.Count == 1)
        {
            vo.RebuildMeshAndPhysics(_worldManager.PhysicsWorld);
        }
        else
        {
            clusters.Sort((a, b) => b.Count.CompareTo(a.Count));

            var mainCluster = clusters[0];
            var mainSet = new HashSet<Vector3i>(mainCluster);
            vo.VoxelCoordinates.RemoveAll(v => !mainSet.Contains(v));

            var savedPos = vo.Position;
            var savedCoM = vo.LocalCenterOfMass;
            var savedRot = vo.Rotation;

            vo.RebuildMeshAndPhysics(_worldManager.PhysicsWorld);

            for (int i = 1; i < clusters.Count; i++)
                SpawnSplitCluster(clusters[i], vo, savedPos, savedCoM, savedRot);
        }
    }

    private void SpawnSplitCluster(
        List<Vector3i> localClusterVoxels,
        VoxelObject parentObj,
        Vector3 savedParentPos,
        Vector3 savedParentCoM,
        Quaternion savedParentRot)
    {
        if (localClusterVoxels.Count == 0) return;

        Vector3i anchorIdx = localClusterVoxels[0];
        var newLocalVoxels = new List<Vector3i>();
        var newMaterials = new Dictionary<Vector3i, uint>();

        foreach (var originalPos in localClusterVoxels)
        {
            Vector3i newPos = originalPos - anchorIdx;
            newLocalVoxels.Add(newPos);
            if (parentObj.VoxelMaterials.TryGetValue(originalPos, out uint matId))
                newMaterials[newPos] = matId;
            else
                newMaterials[newPos] = (uint)parentObj.Material;
        }

        System.Numerics.Vector3 anchorPosInParentLocal =
            (anchorIdx.ToSystemNumerics() - savedParentCoM.ToSystemNumerics())
            * Constants.VoxelSize * parentObj.Scale;

        System.Numerics.Vector3 anchorWorldPos = savedParentPos.ToSystemNumerics() +
            System.Numerics.Vector3.Transform(anchorPosInParentLocal, savedParentRot.ToSystemNumerics());

        var newObj = new VoxelObject(newLocalVoxels, parentObj.Material, parentObj.Scale, newMaterials);
        newObj.OnEmpty += QueueForRemoval;

        var handle = _worldManager.PhysicsWorld.CreateVoxelObjectBody(
            newLocalVoxels, newObj.Material, newObj.Scale, anchorWorldPos, out var newBodyCoM);

        if (handle.Value == -1) return;

        var bodyRef = _worldManager.PhysicsWorld.Simulation.Bodies.GetBodyReference(handle);
        System.Numerics.Vector3 rotatedCoMOffset = System.Numerics.Vector3.Transform(
            newBodyCoM, savedParentRot.ToSystemNumerics());

        bodyRef.Pose.Position = anchorWorldPos + rotatedCoMOffset;
        bodyRef.Pose.Orientation = savedParentRot.ToSystemNumerics();

        if (_worldManager.PhysicsWorld.Simulation.Bodies.BodyExists(parentObj.BodyHandle))
            bodyRef.Velocity = _worldManager.PhysicsWorld.Simulation.Bodies.GetBodyReference(parentObj.BodyHandle).Velocity;

        newObj.InitializePhysics(handle, newBodyCoM.ToOpenTK());

        lock (_objectsLock)
        {
            _voxelObjects.Add(newObj);
            _bodyToVoxelObjectMap[handle] = newObj;
        }
    }

    // -------------------------------------------------------------------------
    // Тесты
    // -------------------------------------------------------------------------

    public void TestBreakVoxel(VoxelObject vo, Vector3i localPos)
    {
        if (!vo.RemoveVoxel(localPos)) return;
        if (vo.VoxelCoordinates.Count == 0) return;

        var clusters = vo.GetConnectedClusters();
        if (clusters.Count == 1)
        {
            vo.RebuildMeshAndPhysics(_worldManager.PhysicsWorld);
        }
        else
        {
            clusters.Sort((a, b) => b.Count.CompareTo(a.Count));
            var mainSet = new HashSet<Vector3i>(clusters[0]);
            vo.VoxelCoordinates.RemoveAll(v => !mainSet.Contains(v));

            var savedPos = vo.Position;
            var savedCoM = vo.LocalCenterOfMass;
            var savedRot = vo.Rotation;

            vo.RebuildMeshAndPhysics(_worldManager.PhysicsWorld);

            for (int i = 1; i < clusters.Count; i++)
                SpawnSplitCluster(clusters[i], vo, savedPos, savedCoM, savedRot);
        }
    }
}