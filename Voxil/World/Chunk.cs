// /World/Chunk.cs - ПОЛНОСТЬЮ ИСПРАВЛЕН
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

    private List<float> _pendingVertices;
    private List<float> _pendingColors;
    private List<float> _pendingAoValues;

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

        // Если физики ещё нет - кешируем меш
        if (!HasPhysics && _renderer == null)
        {
            _pendingVertices = vertices;
            _pendingColors = colors;
            _pendingAoValues = aoValues;
            return;
        }

        // Применяем меш
        if (_renderer == null)
        {
            _renderer = new VoxelObjectRenderer(vertices, colors, aoValues);
        }
        else
        {
            _renderer.UpdateMesh(vertices, colors, aoValues);
        }

        // Очищаем кеш
        _pendingVertices = null;
        _pendingColors = null;
        _pendingAoValues = null;
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
            WorldManager.RebuildChunkMeshAsync(this, priority: 0);
            WorldManager.QueuePhysicsRebuild(this, priority: 0);
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
                try
                {
                    if (WorldManager.PhysicsWorld.Simulation.Statics.StaticExists(handle))
                    {
                        var staticRef = WorldManager.PhysicsWorld.Simulation.Statics.GetStaticReference(handle);
                        var shapeIndex = staticRef.Shape;

                        // 1. Удаляем статическое тело
                        WorldManager.PhysicsWorld.Simulation.Statics.Remove(handle);
                        WorldManager.UnregisterChunkStatic(handle);

                        // 2. Удаляем его форму ИЗ СИМУЛЯЦИИ
                        if (shapeIndex.Exists)
                        {
                            WorldManager.PhysicsWorld.Simulation.Shapes.Remove(shapeIndex);

                            // 3. ВОЗВРАЩАЕМ ЕГО БУФЕР В ПУЛ!
                            WorldManager.PhysicsWorld.ReturnCompoundBuffer(shapeIndex);
                        }

                        // 3. Убираем его из словаря WorldManager'а
                        WorldManager.UnregisterChunkStatic(handle);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Chunk {Position}] Error disposing static handle: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Chunk {Position}] Error disposing physics collection: {ex.Message}");
        }

        _hasStaticBody = false;
        _staticHandles.Clear();
    }

    /// <summary>
    /// КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: Правильная очистка физических ресурсов
    /// </summary>
    /// <summary>
    /// КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: Правильная очистка и пересоздание физических ресурсов (единый Compound)
    /// </summary>
    public void RebuildPhysics()
    {
        // 1. СНАЧАЛА полностью очищаем старые физические ресурсы.
        ClearPhysics();

        // 2. ЗАЩИТА от гонки потоков: если чанк уже выгружен, ничего не делаем.
        if (Voxels == null || !IsLoaded) return;

        // 3. СОЗДАЕМ новые физические ресурсы.
        var worldPosition = (Position * Chunk.ChunkSize).ToSystemNumerics();

        Dictionary<Vector3i, MaterialType> voxelsCopy;
        lock (VoxelsLock)
        {
            if (Voxels == null) return;
            voxelsCopy = new Dictionary<Vector3i, MaterialType>(Voxels);
        }

        // Вызываем исправленный метод в PhysicsWorld, который не течет.
        var handle = WorldManager.PhysicsWorld.CreateStaticChunkBody(worldPosition, voxelsCopy);

        if (handle.Value != 0)
        {
            _staticHandles.Add(handle);
            _hasStaticBody = true;
            WorldManager.RegisterChunkStatic(handle, this);
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

        // Вызываем наш новый, надежный метод очистки.
        ClearPhysics();

        lock (VoxelsLock)
        {
            Voxels?.Clear();
        }

        _pendingVertices = null;
        _pendingColors = null;
        _pendingAoValues = null;
    }
}