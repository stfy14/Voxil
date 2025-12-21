// /World/Chunk.cs - FINAL ZERO-GARBAGE VERSION
using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

public class Chunk : IDisposable
{
    public const int ChunkSize = 16;
    public Vector3i Position { get; }
    public WorldManager WorldManager { get; }

    public Dictionary<Vector3i, MaterialType> Voxels { get; private set; } = new();
    private readonly ReaderWriterLockSlim _voxelsLock = new ReaderWriterLockSlim();

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
        _voxelsLock.EnterWriteLock();
        try
        {
            Voxels = new Dictionary<Vector3i, MaterialType>(voxels);
            IsLoaded = true;
        }
        finally
        {
            _voxelsLock.ExitWriteLock();
        }
    }

    public void ApplyMesh(List<float> vertices, List<float> colors, List<float> aoValues)
    {
        if (!IsLoaded || _isDisposed) return;
        if (_renderer == null) _renderer = new VoxelObjectRenderer(vertices, colors, aoValues);
        else _renderer.UpdateMesh(vertices, colors, aoValues);
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
                if (_pendingMesh != null) meshDataPool.Enqueue(_pendingMesh);
                _pendingMesh = meshData;
            }
        }
    }

    public bool RemoveVoxelAndUpdate(Vector3i localPosition)
    {
        bool removed;
        _voxelsLock.EnterWriteLock();
        try
        {
            removed = Voxels.Remove(localPosition);
        }
        finally
        {
            _voxelsLock.ExitWriteLock();
        }

        if (removed && IsLoaded)
        {
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

    public Dictionary<Vector3i, MaterialType> GetSurfaceVoxels(Chunk neighbor_NX, Chunk neighbor_PX, Chunk neighbor_NZ, Chunk neighbor_PZ)
    {
        var surfaceVoxels = new Dictionary<Vector3i, MaterialType>();
        var localVoxels = new Dictionary<Vector3i, MaterialType>();
        CopyVoxelsData(localVoxels); // Используем новый безопасный метод

        if (localVoxels.Count == 0) return surfaceVoxels;

        foreach (var kvp in localVoxels)
        {
            var pos = kvp.Key;
            var material = kvp.Value;

            if (!MaterialRegistry.IsSolidForPhysics(material)) continue;

            bool IsNeighborSolid(Vector3i localPos)
            {
                if (localPos.X >= 0 && localPos.X < ChunkSize && localPos.Z >= 0 && localPos.Z < ChunkSize && localPos.Y >= 0)
                {
                    return localVoxels.TryGetValue(localPos, out var m) && MaterialRegistry.IsSolidForPhysics(m);
                }

                var posInNeighbor = Vector3i.Zero;
                Chunk neighborChunk = null;
                if (localPos.X < 0) { neighborChunk = neighbor_NX; posInNeighbor = localPos + new Vector3i(ChunkSize, 0, 0); }
                else if (localPos.X >= ChunkSize) { neighborChunk = neighbor_PX; posInNeighbor = localPos - new Vector3i(ChunkSize, 0, 0); }
                else if (localPos.Z < 0) { neighborChunk = neighbor_NZ; posInNeighbor = localPos + new Vector3i(0, 0, ChunkSize); }
                else if (localPos.Z >= ChunkSize) { neighborChunk = neighbor_PZ; posInNeighbor = localPos - new Vector3i(0, 0, ChunkSize); }

                return neighborChunk != null && neighborChunk.IsLoaded && neighborChunk.IsVoxelSolidAt(posInNeighbor);
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
        _voxelsLock.EnterWriteLock();
        try
        {
            Voxels?.Clear();
        }
        finally
        {
            _voxelsLock.ExitWriteLock();
        }
        _voxelsLock.Dispose();
    }

    // --- НОВЫЕ ПУБЛИЧНЫЕ БЕЗОПАСНЫЕ МЕТОДЫ ---
    public bool IsVoxelSolidAt(Vector3i localPos)
    {
        _voxelsLock.EnterReadLock();
        try
        {
            return Voxels.TryGetValue(localPos, out var mat) && MaterialRegistry.IsSolidForPhysics(mat);
        }
        finally
        {
            _voxelsLock.ExitReadLock();
        }
    }

    public void CopyVoxelsData(Dictionary<Vector3i, MaterialType> targetDictionary)
    {
        targetDictionary.Clear();
        _voxelsLock.EnterReadLock();
        try
        {
            foreach (var kvp in Voxels)
            {
                targetDictionary.Add(kvp.Key, kvp.Value);
            }
        }
        finally
        {
            _voxelsLock.ExitReadLock();
        }
    }
}