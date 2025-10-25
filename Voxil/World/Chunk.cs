// /World/Chunk.cs - FINAL, OPTIMIZED VERSION
using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

public class Chunk : IDisposable
{
    public const int ChunkSize = 16;
    public Vector3i Position { get; }
    public WorldManager WorldManager { get; }

    public Dictionary<Vector3i, MaterialType> Voxels { get; private set; } = new();
    public readonly object VoxelsLock = new();

    private VoxelObjectRenderer _renderer;
    private List<StaticHandle> _staticHandles = new List<StaticHandle>();
    private bool _hasStaticBody = false;

    public bool IsLoaded { get; private set; } = false;
    public bool HasPhysics => _hasStaticBody;
    private bool _isDisposed = false;
    public int StaticHandlesCount => _staticHandles.Count;

    private FinalizedMeshData _pendingMesh = null;
    private readonly object _stateLock = new object();

    public Chunk(Vector3i position, WorldManager worldManager)
    {
        Position = position;
        WorldManager = worldManager;
    }

    public void SetVoxelData(Dictionary<Vector3i, MaterialType> voxels)
    {
        lock (VoxelsLock)
        {
            Voxels = new Dictionary<Vector3i, MaterialType>(voxels);
            IsLoaded = true;
        }
    }

    public void ApplyMesh(List<float> vertices, List<float> colors, List<float> aoValues)
    {
        if (!IsLoaded || _isDisposed) return;
        if (_renderer == null)
        {
            _renderer = new VoxelObjectRenderer(vertices, colors, aoValues);
        }
        else
        {
            _renderer.UpdateMesh(vertices, colors, aoValues);
        }
    }

    public void TrySetMesh(FinalizedMeshData meshData, ConcurrentQueue<FinalizedMeshData> meshDataPool)
    {
        lock (_stateLock)
        {
            if (HasPhysics)
            {
                ApplyMesh(meshData.Vertices, meshData.Colors, meshData.AoValues);
                meshDataPool.Enqueue(meshData);
            }
            else
            {
                if (_pendingMesh != null)
                {
                    meshDataPool.Enqueue(_pendingMesh);
                }
                _pendingMesh = meshData;
            }
        }
    }

    public bool RemoveVoxelAndUpdate(Vector3i localPosition)
    {
        bool removed;
        lock (VoxelsLock)
        {
            removed = Voxels.Remove(localPosition);
        }
        if (removed && IsLoaded)
        {
            // Эта одна функция теперь ставит в очередь и меш, и физику.
            WorldManager.RebuildChunkAsync(this);
            WorldManager.NotifyNeighborsOfVoxelChange(Position, localPosition);
            WorldManager.QueueDetachmentCheck(this, localPosition);
        }
        return removed;
    }

    private void ClearPhysics()
    {
        if (!_hasStaticBody) return;
        try
        {
            foreach (var handle in _staticHandles)
            {
                if (WorldManager.PhysicsWorld.Simulation.Statics.StaticExists(handle))
                {
                    var staticRef = WorldManager.PhysicsWorld.Simulation.Statics.GetStaticReference(handle);
                    var shapeIndex = staticRef.Shape;
                    WorldManager.PhysicsWorld.Simulation.Statics.Remove(handle);
                    if (shapeIndex.Exists)
                    {
                        WorldManager.PhysicsWorld.Simulation.Shapes.Remove(shapeIndex);
                        WorldManager.PhysicsWorld.ReturnCompoundBuffer(shapeIndex);
                    }
                    WorldManager.UnregisterChunkStatic(handle);
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Chunk {Position}] Error disposing physics: {ex.Message}"); }
        _hasStaticBody = false;
        _staticHandles.Clear();
    }

    public Dictionary<Vector3i, MaterialType> GetSurfaceVoxels(
        Dictionary<Vector3i, MaterialType> neighbor_NX,
        Dictionary<Vector3i, MaterialType> neighbor_PX,
        Dictionary<Vector3i, MaterialType> neighbor_NZ,
        Dictionary<Vector3i, MaterialType> neighbor_PZ)
    {
        lock (VoxelsLock)
        {
            if (Voxels.Count == 0) return new Dictionary<Vector3i, MaterialType>();

            var surfaceVoxels = new Dictionary<Vector3i, MaterialType>();

            foreach (var kvp in Voxels)
            {
                var pos = kvp.Key;
                var material = kvp.Value;

                if (!MaterialRegistry.IsSolidForPhysics(material))
                {
                    continue;
                }

                bool IsNeighborSolid(Vector3i localPos)
                {
                    if (localPos.X >= 0 && localPos.X < ChunkSize &&
                        localPos.Z >= 0 && localPos.Z < ChunkSize &&
                        localPos.Y >= 0)
                    {
                        return Voxels.TryGetValue(localPos, out var neighborMaterial) &&
                               MaterialRegistry.IsSolidForPhysics(neighborMaterial);
                    }

                    if (localPos.X < 0)
                        return neighbor_NX != null && neighbor_NX.TryGetValue(localPos + new Vector3i(ChunkSize, 0, 0), out var mat) && MaterialRegistry.IsSolidForPhysics(mat);
                    if (localPos.X >= ChunkSize)
                        return neighbor_PX != null && neighbor_PX.TryGetValue(localPos - new Vector3i(ChunkSize, 0, 0), out var mat) && MaterialRegistry.IsSolidForPhysics(mat);
                    if (localPos.Z < 0)
                        return neighbor_NZ != null && neighbor_NZ.TryGetValue(localPos + new Vector3i(0, 0, ChunkSize), out var mat) && MaterialRegistry.IsSolidForPhysics(mat);
                    if (localPos.Z >= ChunkSize)
                        return neighbor_PZ != null && neighbor_PZ.TryGetValue(localPos - new Vector3i(0, 0, ChunkSize), out var mat) && MaterialRegistry.IsSolidForPhysics(mat);

                    return false;
                }

                if (!IsNeighborSolid(pos + Vector3i.UnitX) || !IsNeighborSolid(pos - Vector3i.UnitX) ||
                    !IsNeighborSolid(pos + Vector3i.UnitY) || !IsNeighborSolid(pos - Vector3i.UnitY) ||
                    !IsNeighborSolid(pos + Vector3i.UnitZ) || !IsNeighborSolid(pos - Vector3i.UnitZ))
                {
                    surfaceVoxels.Add(pos, material);
                }
            }
            return surfaceVoxels;
        }
    }

    public void OnPhysicsRebuilt(StaticHandle handle, ConcurrentQueue<FinalizedMeshData> meshDataPool)
    {
        lock (_stateLock)
        {
            ClearPhysics();
            _hasStaticBody = false;

            if (WorldManager.PhysicsWorld.Simulation.Statics.StaticExists(handle))
            {
                _staticHandles.Add(handle);
                WorldManager.RegisterChunkStatic(handle, this);
                _hasStaticBody = true;
            }

            if (_hasStaticBody && _pendingMesh != null)
            {
                ApplyMesh(_pendingMesh.Vertices, _pendingMesh.Colors, _pendingMesh.AoValues);
                meshDataPool.Enqueue(_pendingMesh);
                _pendingMesh = null;
            }
        }
    }

    public void Render(Shader shader, Matrix4 view, Matrix4 projection)
    {
        if (!IsLoaded || _renderer == null || _isDisposed) return;
        var worldPosition = Position * ChunkSize;
        Matrix4 model = Matrix4.CreateTranslation(worldPosition.X, worldPosition.Y, worldPosition.Z);
        _renderer.Render(shader, model, view, projection);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        IsLoaded = false;
        _renderer?.Dispose();
        _renderer = null;
        ClearPhysics();
        lock (VoxelsLock)
        {
            Voxels?.Clear();
        }
    }
}