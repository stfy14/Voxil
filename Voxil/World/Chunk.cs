// /World/Chunk.cs
using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;

public class Chunk : IDisposable
{
    public const int ChunkSize = 16;
    public Vector3i Position { get; }
    public WorldManager WorldManager { get; }

    public Dictionary<Vector3i, MaterialType> Voxels { get; private set; } = new();
    private VoxelObjectRenderer? _renderer;
    private StaticHandle _staticHandle;
    private bool _hasStaticBody = false;
    public bool _isLoaded = false;

    public Chunk(Vector3i position, WorldManager worldManager)
    {
        Position = position;
        WorldManager = worldManager;
    }

    /// <summary>
    /// Шаг 1: Сохраняет данные о вокселях и СРАЗУ ставит в очередь физику.
    /// </summary>
    public void SetVoxelDataAndQueuePhysics(Dictionary<Vector3i, MaterialType> voxels)
    {
        this.Voxels = voxels;
        _isLoaded = true;
        // Делаем чанк "твердым" как можно раньше
        WorldManager.QueueForPhysicsRebuild(this);
    }

    /// <summary>
    /// Шаг 2: Применяет финальный, готовый меш, делая чанк видимым.
    /// </summary>
    public void ApplyFinalizedMesh(FinalizedChunkData data)
    {
        if (!_isLoaded) return;

        if (_renderer == null)
            _renderer = new VoxelObjectRenderer(data.Vertices, data.Colors, data.AoValues);
        else
            _renderer.UpdateMesh(data.Vertices, data.Colors, data.AoValues);
    }

    public void RebuildMeshAsync()
    {
        if (!_isLoaded) return;

        Func<Vector3i, bool> solidCheckFunc = localPos => IsVoxelSolidGlobal(localPos);
        WorldManager.QueueForFinalization(new ChunkFinalizeRequest
        {
            Position = this.Position,
            Voxels = this.Voxels,
            IsVoxelSolidGlobalFunc = solidCheckFunc
        });
    }

    public bool RemoveVoxelAndUpdate(Vector3i localPosition)
    {
        if (Voxels.Remove(localPosition))
        {
            if (_isLoaded)
            {
                RebuildMeshAsync(); // Асинхронно обновляем свой меш
                WorldManager.NotifyNeighborsOfVoxelChange(Position, localPosition); // Асинхронно обновляем соседей
                WorldManager.QueueForDetachmentCheck(this, localPosition); // Асинхронно проверяем отрыв
            }
            return true;
        }
        return false;
    }

    public void RebuildPhysics()
    {
        if (!_isLoaded) return;

        if (_hasStaticBody)
        {
            var staticRef = WorldManager.PhysicsWorld.Simulation.Statics.GetStaticReference(_staticHandle);
            var shapeIndex = staticRef.Shape;
            WorldManager.PhysicsWorld.Simulation.Statics.Remove(_staticHandle);
            WorldManager.PhysicsWorld.Simulation.Shapes.Remove(shapeIndex);
            WorldManager.UnregisterChunkStatic(_staticHandle);
            _hasStaticBody = false;
        }

        if (Voxels.Count > 0)
        {
            var worldPosition = (Position * ChunkSize).ToSystemNumerics();
            var voxelCoordinates = Voxels.Keys.ToList();
            _staticHandle = WorldManager.PhysicsWorld.CreateStaticVoxelBody(worldPosition, voxelCoordinates);
            if (_staticHandle.Value != 0)
            {
                _hasStaticBody = true;
                WorldManager.RegisterChunkStatic(_staticHandle, this);
            }
        }
    }

    private bool IsVoxelSolidGlobal(Vector3i localPos)
    {
        Vector3i worldPos = (Position * ChunkSize) + localPos;
        return WorldManager.IsVoxelSolidWorld(worldPos);
    }

    public void Render(Shader shader, Matrix4 view, Matrix4 projection)
    {
        if (!_isLoaded || _renderer == null) return;
        var worldPosition = Position * ChunkSize;
        Matrix4 model = Matrix4.CreateTranslation(worldPosition.X, worldPosition.Y, worldPosition.Z);
        _renderer.Render(shader, model, view, projection);
    }

    public void Unload()
    {
        if (!_isLoaded) return;
        _isLoaded = false;

        _renderer?.Dispose();
        _renderer = null;

        if (_hasStaticBody)
        {
            var staticRef = WorldManager.PhysicsWorld.Simulation.Statics.GetStaticReference(_staticHandle);
            WorldManager.PhysicsWorld.Simulation.Statics.Remove(_staticHandle);
            WorldManager.PhysicsWorld.Simulation.Shapes.Remove(staticRef.Shape);
            WorldManager.UnregisterChunkStatic(_staticHandle);
            _hasStaticBody = false;
            _staticHandle = default;
        }
    }

    public void Dispose()
    {
        Unload();
        Voxels.Clear();
    }
}