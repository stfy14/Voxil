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
    private bool _physicsBodyInitialized = false;

    private readonly List<VoxelObject> _voxelObjects = new();

    // НОВОЕ: Отложенная перестройка физики
    private bool _needsPhysicsRebuild = false;
    private float _physicsRebuildTimer = 0f;
    private const float PhysicsRebuildDelay = 0.1f; // 100ms задержка

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
        BuildMesh(); // Меш обновляем сразу
        // Физику помечаем для отложенного обновления
        _needsPhysicsRebuild = true;
        _physicsRebuildTimer = PhysicsRebuildDelay;
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
            _staticHandle = physicsWorld.CreateStaticVoxelBody(worldPosition, _voxels.ToList());
            WorldManager.RegisterChunkStatic(_staticHandle, this);
            _physicsBodyInitialized = true;
        }
        else
        {
            _physicsBodyInitialized = false;
        }

        _needsPhysicsRebuild = false;
    }

    public bool RemoveVoxelAt(Vector3i localPosition)
    {
        if (_voxels.Remove(localPosition))
        {
            Rebuild(); // Пометит для отложенного обновления
            return true;
        }
        return false;
    }

    public void AddVoxelObject(VoxelObject obj) => _voxelObjects.Add(obj);
    public void RemoveVoxelObject(VoxelObject obj) => _voxelObjects.Remove(obj);

    public void Update(float deltaTime)
    {
        // НОВОЕ: Отложенное обновление физики
        if (_needsPhysicsRebuild)
        {
            _physicsRebuildTimer -= deltaTime;
            if (_physicsRebuildTimer <= 0f)
            {
                InitializePhysics();
            }
        }

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

        if (_physicsBodyInitialized)
        {
            WorldManager.PhysicsWorld.Simulation.Statics.Remove(_staticHandle);
            _physicsBodyInitialized = false;
        }

        _renderer?.Dispose();
    }
}