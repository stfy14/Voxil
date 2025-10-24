// /World/Chunk.cs - FINAL, TRULY FAST PERFORMANCE VERSION
using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
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

    public bool RemoveVoxelAndUpdate(Vector3i localPosition)
    {
        bool removed;
        lock (VoxelsLock)
        {
            removed = Voxels.Remove(localPosition);
        }
        if (removed && IsLoaded)
        {
            WorldManager.RebuildChunkMeshAsync(this);
            WorldManager.RebuildChunkPhysicsAsync(this);
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

    // --- НАСТОЯЩИЙ БЫСТРЫЙ АЛГОРИТМ ---
    public Dictionary<Vector3i, MaterialType> GetSurfaceVoxels()
    {
        lock (VoxelsLock)
        {
            if (Voxels.Count == 0) return new Dictionary<Vector3i, MaterialType>();

            // Мы сразу создаем финальный словарь, без промежуточных HashSet.
            var surfaceVoxels = new Dictionary<Vector3i, MaterialType>();

            // Проходим по всем твердым вокселям в чанке
            foreach (var kvp in Voxels)
            {
                var pos = kvp.Key;

                // Проверяем 6 соседей. Если находим ХОТЯ БЫ ОДНОГО соседа-воздуха,
                // то этот блок - поверхностный. Добавляем его и НЕМЕДЛЕННО
                // переходим к следующему блоку в основном цикле `foreach`.
                if (!Voxels.ContainsKey(pos + Vector3i.UnitX))
                {
                    surfaceVoxels.Add(pos, kvp.Value);
                    continue; // <- Ключевая оптимизация
                }
                if (!Voxels.ContainsKey(pos - Vector3i.UnitX))
                {
                    surfaceVoxels.Add(pos, kvp.Value);
                    continue;
                }
                if (!Voxels.ContainsKey(pos + Vector3i.UnitY))
                {
                    surfaceVoxels.Add(pos, kvp.Value);
                    continue;
                }
                if (!Voxels.ContainsKey(pos - Vector3i.UnitY))
                {
                    surfaceVoxels.Add(pos, kvp.Value);
                    continue;
                }
                if (!Voxels.ContainsKey(pos + Vector3i.UnitZ))
                {
                    surfaceVoxels.Add(pos, kvp.Value);
                    continue;
                }
                if (!Voxels.ContainsKey(pos - Vector3i.UnitZ))
                {
                    surfaceVoxels.Add(pos, kvp.Value);
                    continue;
                }
            }
            return surfaceVoxels;
        }
    }

    public void OnPhysicsRebuilt(StaticHandle handle)
    {
        ClearPhysics();
        _staticHandles.Add(handle);
        WorldManager.RegisterChunkStatic(handle, this);
        _hasStaticBody = (handle.Value != 0);
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