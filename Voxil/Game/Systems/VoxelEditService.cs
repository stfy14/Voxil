using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BepuVector3 = System.Numerics.Vector3;

/// <summary>
/// Отвечает за чтение и изменение вокселей статического мира.
/// </summary>
public class VoxelEditService : IVoxelEditService
{
    private readonly WorldManager _worldManager;
    private readonly HashSet<Chunk> _dirtyChunks = new();
    private readonly ConcurrentDictionary<Vector3i, float> _staticVoxelHealth = new();

    public VoxelEditService(WorldManager worldManager)
    {
        _worldManager = worldManager;
    }

    // -------------------------------------------------------------------------
    // Чтение
    // -------------------------------------------------------------------------

    public MaterialType GetMaterialGlobal(Vector3i globalPos)
    {
        var chunks = _worldManager.GetAllChunks();
        Vector3i chunkPos = _worldManager.GetChunkPosFromVoxelIndex(globalPos);
        lock (chunks)
        {
            if (chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded)
            {
                int res = Constants.ChunkResolution;
                int lx = globalPos.X % res; if (lx < 0) lx += res;
                int ly = globalPos.Y % res; if (ly < 0) ly += res;
                int lz = globalPos.Z % res; if (lz < 0) lz += res;
                return chunk.GetMaterialAt(new Vector3i(lx, ly, lz));
            }
        }
        return MaterialType.Air;
    }

    public bool IsVoxelSolidGlobal(Vector3i globalPos)
    {
        var chunks = _worldManager.GetAllChunks();
        Vector3i chunkPos = _worldManager.GetChunkPosFromVoxelIndex(globalPos);
        lock (chunks)
        {
            if (chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded)
            {
                int res = Constants.ChunkResolution;
                int lx = globalPos.X % res; if (lx < 0) lx += res;
                int ly = globalPos.Y % res; if (ly < 0) ly += res;
                int lz = globalPos.Z % res; if (lz < 0) lz += res;
                return chunk.IsVoxelSolidAt(new Vector3i(lx, ly, lz));
            }
        }
        return false;
    }

    public void GetStaticVoxelHealthInfo(Vector3i globalPos, out float currentHP, out float maxHP)
    {
        var mat = GetMaterialGlobal(globalPos);
        maxHP = MaterialRegistry.Get(mat).Hardness;
        currentHP = _staticVoxelHealth.TryGetValue(globalPos, out float hp) ? hp : maxHP;
    }

    // -------------------------------------------------------------------------
    // Изменение
    // -------------------------------------------------------------------------

    public bool RemoveVoxelGlobal(Vector3i globalPos) => RemoveVoxelGlobal(globalPos, true);

    public bool RemoveVoxelGlobal(Vector3i globalPos, bool updateMesh)
    {
        var chunks = _worldManager.GetAllChunks();
        Vector3i chunkPos = _worldManager.GetChunkPosFromVoxelIndex(globalPos);
        lock (chunks)
        {
            if (!chunks.TryGetValue(chunkPos, out var chunk) || !chunk.IsLoaded) return false;

            int res = Constants.ChunkResolution;
            int lx = globalPos.X % res; if (lx < 0) lx += res;
            int ly = globalPos.Y % res; if (ly < 0) ly += res;
            int lz = globalPos.Z % res; if (lz < 0) lz += res;

            bool removed = chunk.RemoveVoxelAndUpdate(new Vector3i(lx, ly, lz));
            if (removed && !updateMesh)
                lock (_dirtyChunks) _dirtyChunks.Add(chunk);

            return removed;
        }
    }

    public void ApplyDamageToStatic(Vector3i globalPos, float damage, out bool destroyed)
    {
        destroyed = false;
        var mat = GetMaterialGlobal(globalPos);
        if (mat == MaterialType.Air) return;

        float maxHealth = MaterialRegistry.Get(mat).Hardness;

        float newHealth = _staticVoxelHealth.AddOrUpdate(
            globalPos,
            maxHealth - damage,
            (k, current) => current - damage);

        if (newHealth <= 0)
        {
            _staticVoxelHealth.TryRemove(globalPos, out _);
            if (RemoveVoxelGlobal(globalPos, false))
            {
                _worldManager.NotifyVoxelFastDestroyed(globalPos);
                // Integrity check через WorldManager чтобы не тащить зависимость
                EventBus.Publish(new IntegrityCheckEvent(globalPos));
                destroyed = true;
            }
        }
    }

    public void DestroyVoxelAt(CollidableReference collidable, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        var pointInside = worldHitLocation - worldHitNormal * (Constants.VoxelSize * 0.5f);

        if (collidable.Mobility == CollidableMobility.Static)
        {
            Vector3i globalVoxelIndex = new Vector3i(
                (int)Math.Floor(pointInside.X / Constants.VoxelSize),
                (int)Math.Floor(pointInside.Y / Constants.VoxelSize),
                (int)Math.Floor(pointInside.Z / Constants.VoxelSize));

            if (RemoveVoxelGlobal(globalVoxelIndex))
            {
                _worldManager.NotifyVoxelFastDestroyed(globalVoxelIndex);
                EventBus.Publish(new IntegrityCheckEvent(globalVoxelIndex));
            }
        }
        else if (collidable.Mobility == CollidableMobility.Dynamic)
        {
            var objService = ServiceLocator.Get<IVoxelObjectService>();
            var allObjects = objService.GetAllVoxelObjects();

            // Ищем объект по BodyHandle через физику
            var physics = _worldManager.PhysicsWorld;
            if (!physics.Simulation.Bodies.BodyExists(collidable.BodyHandle)) return;

            VoxelObject voxelObj = null;
            foreach (var vo in allObjects)
            {
                if (vo.BodyHandle.Value == collidable.BodyHandle.Value)
                {
                    voxelObj = vo;
                    break;
                }
            }
            if (voxelObj == null) return;

            float alpha = physics.PhysicsAlpha;
            Matrix4 modelMatrix = voxelObj.GetInterpolatedModelMatrix(alpha);
            Matrix4 invModel = Matrix4.Invert(modelMatrix);

            Vector4 localHitRaw = new Vector4(worldHitLocation.ToOpenTK(), 1.0f) * invModel;
            Vector3 localHitPos = localHitRaw.Xyz;

            Quaternion invRot = Quaternion.Invert(Quaternion.Slerp(voxelObj.PrevRotation, voxelObj.Rotation, alpha));
            Vector3 localNormal = Vector3.Transform(worldHitNormal.ToOpenTK(), invRot);
            Vector3 pointInsideLocal = localHitPos - localNormal * (Constants.VoxelSize * 0.5f);

            Vector3i localVoxelIndex = new Vector3i(
                (int)Math.Floor(pointInsideLocal.X / Constants.VoxelSize),
                (int)Math.Floor(pointInsideLocal.Y / Constants.VoxelSize),
                (int)Math.Floor(pointInsideLocal.Z / Constants.VoxelSize));

            if (voxelObj.RemoveVoxel(localVoxelIndex))
                objService.ProcessDynamicObjectSplits(voxelObj);
        }
    }

    // -------------------------------------------------------------------------
    // Грязные чанки
    // -------------------------------------------------------------------------

    public void MarkChunkDirty(Vector3i globalVoxelIndex)
    {
        var chunks = _worldManager.GetAllChunks();
        Vector3i chunkPos = _worldManager.GetChunkPosFromVoxelIndex(globalVoxelIndex);
        lock (chunks)
        {
            if (chunks.TryGetValue(chunkPos, out var chunk))
                lock (_dirtyChunks) _dirtyChunks.Add(chunk);
        }
    }

    public void UpdateDirtyChunks()
    {
        Chunk[] dirtyArr;
        lock (_dirtyChunks)
        {
            dirtyArr = _dirtyChunks.ToArray();
            _dirtyChunks.Clear();
        }

        foreach (var chunk in dirtyArr)
        {
            _worldManager.NotifyChunkModified(chunk);
            _worldManager.RebuildPhysics(chunk);
        }
    }
}