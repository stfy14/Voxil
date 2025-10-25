// /World/Chunk.cs - FINAL, TRULY FAST PERFORMANCE VERSION
using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

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
    private readonly object _stateLock = new object(); // Единый замок для состояния чанка

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

    // --- НОВЫЙ МЕТОД ---
    public void TrySetMesh(FinalizedMeshData meshData, ConcurrentQueue<FinalizedMeshData> meshDataPool)
    {
        if (Position == Vector3i.Zero) Console.WriteLine($"---> [TRACE 0,0,0] TrySetMesh called. Current HasPhysics is {HasPhysics}.");
        lock (_stateLock)
        {
            if (HasPhysics)
            {
                if (Position == Vector3i.Zero) Console.WriteLine("     [TRACE 0,0,0] Applying mesh directly.");
                ApplyMesh(meshData.Vertices, meshData.Colors, meshData.AoValues);
                meshDataPool.Enqueue(meshData);
            }
            else
            {
                if (Position == Vector3i.Zero) Console.WriteLine("     [TRACE 0,0,0] Storing mesh as PENDING.");
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

            var surfaceVoxels = new Dictionary<Vector3i, MaterialType>();

            foreach (var kvp in Voxels)
            {
                var pos = kvp.Key;
                var material = kvp.Value;

                // Пропускаем материалы, у которых не должно быть коллайдеров
                if (!MaterialRegistry.IsSolidForPhysics(material))
                {
                    continue;
                }

                // Функция-помощник для проверки соседа
                bool IsNeighborSolid(Vector3i neighborPos)
                {
                    return Voxels.TryGetValue(neighborPos, out var neighborMaterial) &&
                           MaterialRegistry.IsSolidForPhysics(neighborMaterial);
                }

                // --- ИСПРАВЛЕНИЕ ЛОГИКИ ---
                // Воксель является поверхностью, если ХОТЯ БЫ ОДИН из его 6 соседей не является твердым.
                bool isSurface = !IsNeighborSolid(pos + Vector3i.UnitX) ||
                                 !IsNeighborSolid(pos - Vector3i.UnitX) ||
                                 !IsNeighborSolid(pos + Vector3i.UnitY) ||
                                 !IsNeighborSolid(pos - Vector3i.UnitY) ||
                                 !IsNeighborSolid(pos + Vector3i.UnitZ) ||
                                 !IsNeighborSolid(pos - Vector3i.UnitZ);

                if (isSurface)
                {
                    surfaceVoxels.Add(pos, material);
                }
            }
            return surfaceVoxels;
        }
    }

    public void OnPhysicsRebuilt(StaticHandle handle, ConcurrentQueue<FinalizedMeshData> meshDataPool)
    {
        if (Position == Vector3i.Zero) Console.WriteLine($"---> [TRACE 0,0,0] OnPhysicsRebuilt called. Handle value: {handle.Value}.");
        lock (_stateLock)
        {
            ClearPhysics();
            _hasStaticBody = false; // Сначала сбрасываем флаг

            // --- ГЛАВНОЕ ИСПРАВЛЕНИЕ ---
            // Проверяем, что полученный хэндл действителен. У дефолтного/невалидного хэндла значение 0.
            if (handle.Value != 0 && WorldManager.PhysicsWorld.Simulation.Statics.StaticExists(handle))
            {
                _staticHandles.Add(handle);
                WorldManager.RegisterChunkStatic(handle, this);
                _hasStaticBody = true; // Устанавливаем флаг, только если хэндл валиден
            }

            if (Position == Vector3i.Zero)
            {
                if (_pendingMesh != null)
                    Console.WriteLine($"     [TRACE 0,0,0] Found a pending mesh! Applying it now. HasPhysics is now: {_hasStaticBody}");
                else
                    Console.WriteLine($"     [TRACE 0,0,0] No pending mesh was found. HasPhysics is now: {_hasStaticBody}");
            }

            if (_pendingMesh != null)
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