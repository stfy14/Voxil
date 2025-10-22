// /World/Chunk.cs - REFACTORED
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
    public readonly object VoxelsLock = new(); // Thread-safe доступ к вокселям

    private VoxelObjectRenderer _renderer;
    private List<StaticHandle> _staticHandles = new List<StaticHandle>();
    private bool _hasStaticBody = false;

    public bool IsLoaded { get; private set; } = false;
    public bool HasPhysics => _hasStaticBody; // НОВОЕ: Проверка наличия физики
    private bool _isDisposed = false;

    // НОВОЕ: Кеш меша для отложенного применения
    private List<float> _pendingVertices;
    private List<float> _pendingColors;
    private List<float> _pendingAoValues;

    public Chunk(Vector3i position, WorldManager worldManager)
    {
        Position = position;
        WorldManager = worldManager;
    }

    /// <summary>
    /// Устанавливает данные о вокселях после генерации
    /// </summary>
    public void SetVoxelData(Dictionary<Vector3i, MaterialType> voxels)
    {
        lock (VoxelsLock)
        {
            Voxels = new Dictionary<Vector3i, MaterialType>(voxels);
            IsLoaded = true;
        }
    }

    /// <summary>
    /// Применяет готовый меш из фонового потока
    /// </summary>
    public void ApplyMesh(List<float> vertices, List<float> colors, List<float> aoValues)
    {
        if (!IsLoaded || _isDisposed) return;

        // КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: Если физики ещё нет - кешируем меш
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

        // Очищаем кеш после применения
        _pendingVertices = null;
        _pendingColors = null;
        _pendingAoValues = null;
    }

    /// <summary>
    /// Удаляет воксель и запускает все необходимые обновления
    /// </summary>
    public bool RemoveVoxelAndUpdate(Vector3i localPosition)
    {
        bool removed;
        lock (VoxelsLock)
        {
            removed = Voxels.Remove(localPosition);
        }

        if (removed && IsLoaded)
        {
            // Обновляем меш с высоким приоритетом (игрок ждёт результата)
            WorldManager.RebuildChunkMeshAsync(this, priority: 0);

            // Обновляем физику с высоким приоритетом
            WorldManager.QueuePhysicsRebuild(this, priority: 0);

            // Уведомляем соседей (они обновят свои меши для корректного AO)
            WorldManager.NotifyNeighborsOfVoxelChange(Position, localPosition);

            // Проверяем отсоединение
            WorldManager.QueueDetachmentCheck(this, localPosition);
        }

        return removed;
    }

    /// <summary>
    /// Перестраивает физическое тело чанка
    /// </summary>
    public void RebuildPhysics()
    {
        if (!IsLoaded || _isDisposed) return;

        // Удаляем старое физическое тело (поддерживаем несколько хэндлов)
        if (_hasStaticBody)
        {
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

                            // Сначала удаляем статику
                            WorldManager.PhysicsWorld.Simulation.Statics.Remove(handle);

                            // Потом удаляем форму (если она существует)
                            if (shapeIndex.Exists)
                            {
                                WorldManager.PhysicsWorld.Simulation.Shapes.Remove(shapeIndex);
                            }

                            WorldManager.UnregisterChunkStatic(handle);
                        }
                    }
                    catch (Exception innerEx)
                    {
                        Console.WriteLine($"[Chunk {Position}] Error removing a static handle: {innerEx.Message}");
                    }
                }

                _staticHandles.Clear();
                _hasStaticBody = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Chunk {Position}] Error removing physics body: {ex.Message}");
                _staticHandles.Clear();
                _hasStaticBody = false;
            }
        }

        // Создаём новое физическое тело, если есть воксели
        List<Vector3i> voxelCoordinates;
        lock (VoxelsLock)
        {
            if (Voxels.Count == 0)
            {
                return; // Не логируем - это нормально
            }

            voxelCoordinates = new List<Vector3i>(Voxels.Keys);
        }

        try
        {
            var worldPosition = (Position * ChunkSize).ToSystemNumerics();
            var staticHandles = WorldManager.PhysicsWorld.CreateStaticVoxelBody(worldPosition, voxelCoordinates);

            if (staticHandles != null && staticHandles.Count > 0)
            {
                // Сохраняем все handles
                foreach (var h in staticHandles)
                {
                    _staticHandles.Add(h);
                    WorldManager.RegisterChunkStatic(h, this);
                }

                _hasStaticBody = true;

                // НОВОЕ: Если у нас был отложенный меш - применяем его сейчас
                if (_pendingVertices != null && _pendingColors != null && _pendingAoValues != null)
                {
                    ApplyMesh(_pendingVertices, _pendingColors, _pendingAoValues);
                }
            }
            else
            {
                Console.WriteLine($"[Chunk {Position}] WARNING: Physics creation returned no handles!");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Chunk {Position}] Error creating physics body: {ex.Message}");
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

        if (_hasStaticBody)
        {
            try
            {
                // Удаляем все статик-хэндлы
                foreach (var handle in _staticHandles)
                {
                    try
                    {
                        if (WorldManager.PhysicsWorld.Simulation.Statics.StaticExists(handle))
                        {
                            var staticRef = WorldManager.PhysicsWorld.Simulation.Statics.GetStaticReference(handle);
                            var shapeIndex = staticRef.Shape;

                            WorldManager.PhysicsWorld.Simulation.Statics.Remove(handle);

                            if (shapeIndex.Exists)
                            {
                                WorldManager.PhysicsWorld.Simulation.Shapes.Remove(shapeIndex);
                            }

                            WorldManager.UnregisterChunkStatic(handle);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Chunk {Position}] Error disposing a static handle: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Chunk {Position}] Error disposing physics: {ex.Message}");
            }

            _hasStaticBody = false;
            _staticHandles.Clear();
        }

        lock (VoxelsLock)
        {
            Voxels.Clear();
        }
    }
}