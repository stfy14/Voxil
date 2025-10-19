// /World/Chunk.cs
using BepuPhysics;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;
using BepuVector3 = System.Numerics.Vector3;

public class Chunk : IDisposable
{
    public const int ChunkSize = 16;
    public Vector3i Position { get; }
    public WorldManager WorldManager { get; }

    private readonly HashSet<Vector3i> _voxels = new();
    private VoxelObjectRenderer _renderer;

    private StaticHandle _staticHandle;
    // НОВОЕ ПОЛЕ: Этот флаг будет надежно отслеживать, создана ли физика для чанка.
    private bool _physicsBodyInitialized = false;

    private readonly List<VoxelObject> _voxelObjects = new();

    public Chunk(Vector3i position, WorldManager worldManager)
    {
        Position = position;
        WorldManager = worldManager;
    }

    public void Generate(IWorldGenerator generator)
    {
        generator.GenerateChunk(this, _voxels);
        Rebuild();
    }

    public void Rebuild()
    {
        BuildMesh();
        InitializePhysics();
    }

    private void BuildMesh()
    {
        VoxelMeshBuilder.GenerateMesh(_voxels, MaterialType.Dirt,
            out var vertices, out var colors, out var aoValues);

        if (_renderer == null)
            _renderer = new VoxelObjectRenderer(vertices, colors, aoValues);
        else
            _renderer.UpdateMesh(vertices, colors, aoValues);
    }

    private void InitializePhysics()
    {
        var physicsWorld = WorldManager.PhysicsWorld;

        if (_physicsBodyInitialized)
        {
            physicsWorld.Simulation.Statics.Remove(_staticHandle);
        }

        var worldPosition = (Position * ChunkSize).ToSystemNumerics();

        if (_voxels.Count > 0)
        {
            // ДИАГНОСТИКА: Найдём минимальную и максимальную Y координату
            int minY = int.MaxValue;
            int maxY = int.MinValue;
            foreach (var voxel in _voxels)
            {
                if (voxel.Y < minY) minY = voxel.Y;
                if (voxel.Y > maxY) maxY = voxel.Y;
            }

            Console.WriteLine($"[Chunk] Создание статического физического тела для чанка {Position} с {_voxels.Count} вокселями.");
            Console.WriteLine($"[Chunk] World position: {worldPosition}");
            Console.WriteLine($"[Chunk] Voxels Y range: {minY} to {maxY}");
            Console.WriteLine($"[Chunk] Actual world Y range: {worldPosition.Y + minY} to {worldPosition.Y + maxY}");

            _staticHandle = physicsWorld.CreateStaticVoxelBody(worldPosition, _voxels.ToList());
            WorldManager.RegisterChunkStatic(_staticHandle, this);
            _physicsBodyInitialized = true;
        }
        else
        {
            _physicsBodyInitialized = false;
        }
    }

    public bool RemoveVoxelAt(Vector3i localPosition)
    {
        if (_voxels.Remove(localPosition))
        {
            Rebuild();
            return true;
        }
        return false;
    }

    public void AddVoxelObject(VoxelObject obj) => _voxelObjects.Add(obj);
    public void RemoveVoxelObject(VoxelObject obj) => _voxelObjects.Remove(obj);

    public void Update()
    {
        foreach (var voxelObject in _voxelObjects)
        {
            var pose = WorldManager.PhysicsWorld.GetPose(voxelObject.BodyHandle);
            voxelObject.UpdatePose(pose);
        }
    }

    public void Render(Shader shader, Matrix4 view, Matrix4 projection)
    {
        if (_renderer != null)
        {
            var worldPosition = Position * ChunkSize;
            Matrix4 model = Matrix4.CreateTranslation(worldPosition.X, worldPosition.Y, worldPosition.Z);
            _renderer.Render(shader, model, view, projection);
        }

        foreach (var voxelObject in _voxelObjects)
        {
            voxelObject.Render(shader, view, projection);
        }
    }

    public void Dispose()
    {
        foreach (var obj in new List<VoxelObject>(_voxelObjects))
            WorldManager.QueueForRemoval(obj);
        _voxelObjects.Clear();

        // ИСПРАВЛЕНО: Используем тот же надежный флаг при уничтожении объекта.
        if (_physicsBodyInitialized)
        {
            WorldManager.PhysicsWorld.Simulation.Statics.Remove(_staticHandle);
            _physicsBodyInitialized = false;
        }

        _renderer?.Dispose();
    }
}