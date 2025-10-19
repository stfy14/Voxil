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
    public MaterialType Material { get; private set; } = MaterialType.Dirt;

    public HashSet<Vector3i> Voxels = new();
    private VoxelObjectRenderer _renderer;
    private StaticHandle _staticHandle;
    private bool _hasStaticBody = false;
    public bool _isLoaded = false; //ИСПРАВИТЬ ПОТОМ НА PRIVATE

    private readonly List<VoxelObject> _voxelObjects = new List<VoxelObject>();

    private bool _needsPhysicsRebuild = false;
    private float _physicsRebuildTimer = 0f;
    private const float PhysicsRebuildDelay = 0.1f;

    public Chunk(Vector3i position, WorldManager worldManager)
    {
        Position = position;
        WorldManager = worldManager;
    }

    public void Generate(IWorldGenerator generator)
    {
        generator.GenerateChunk(this, Voxels);
        Load();
    }

    public void Load()
    {
        if (_isLoaded) return;
        BuildMesh();
        _needsPhysicsRebuild = true;
        _physicsRebuildTimer = PhysicsRebuildDelay;
        _isLoaded = true;
    }

    public void Unload()
    {
        if (!_isLoaded) return;

        _renderer?.Dispose();
        _renderer = null;

        if (_hasStaticBody)
        {
            var staticRef = WorldManager.PhysicsWorld.Simulation.Statics.GetStaticReference(_staticHandle);
            var shapeIndex = staticRef.Shape;

            WorldManager.PhysicsWorld.Simulation.Statics.Remove(_staticHandle);

            WorldManager.PhysicsWorld.Simulation.Shapes.Remove(shapeIndex);

            _hasStaticBody = false;
            _staticHandle = default;
        }

        _isLoaded = false;
    }

    public bool RemoveVoxelAndUpdate(Vector3i localPosition)
    {
        if (Voxels.Remove(localPosition))
        {
            if (_isLoaded)
            {
                BuildMesh();
                _needsPhysicsRebuild = true;
                _physicsRebuildTimer = PhysicsRebuildDelay;
            }
            return true;
        }
        return false;
    }

    // ДОБАВЬТЕ этот новый метод. Он просто удаляет воксель из данных, НЕ обновляя меш.
    // Он будет использоваться для удаления больших групп.
    public bool RemoveVoxelAt(Vector3i localPosition)
    {
        return Voxels.Remove(localPosition);
    }

    // ДОБАВЬТЕ этот метод для завершения пакетного удаления.
    public void FinalizeGroupRemoval()
    {
        if (_isLoaded)
        {
            BuildMesh();
            _needsPhysicsRebuild = true;
            _physicsRebuildTimer = PhysicsRebuildDelay;
        }
    }

    public void AddVoxelObject(VoxelObject obj) => _voxelObjects.Add(obj);
    public void RemoveVoxelObject(VoxelObject obj) => _voxelObjects.Remove(obj);

    private void BuildMesh()
    {
        VoxelMeshBuilder.GenerateMesh(Voxels, MaterialType.Dirt, out var vertices, out var colors, out var aoValues);
        if (_renderer == null)
            _renderer = new VoxelObjectRenderer(vertices, colors, aoValues);
        else
            _renderer.UpdateMesh(vertices, colors, aoValues);
    }

    private void RebuildPhysics()
    {
        if (_hasStaticBody)
        {
            var staticRef = WorldManager.PhysicsWorld.Simulation.Statics.GetStaticReference(_staticHandle);
            var oldShapeIndex = staticRef.Shape;

            WorldManager.PhysicsWorld.Simulation.Statics.Remove(_staticHandle);
            WorldManager.PhysicsWorld.Simulation.Shapes.Remove(oldShapeIndex);

            _hasStaticBody = false;
        }

        if (Voxels.Count > 0)
        {
            var worldPosition = (Position * ChunkSize).ToSystemNumerics();
            _staticHandle = WorldManager.PhysicsWorld.CreateStaticVoxelBody(worldPosition, Voxels.ToList());
            _hasStaticBody = true;
            WorldManager.RegisterChunkStatic(_staticHandle, this);
        }

        _needsPhysicsRebuild = false;
    }

    public void Update(float deltaTime)
    {
        if (!_isLoaded) return;

        if (_needsPhysicsRebuild)
        {
            _physicsRebuildTimer -= deltaTime;
            if (_physicsRebuildTimer <= 0f)
            {
                RebuildPhysics();
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
        if (!_isLoaded || _renderer == null) return;
        var worldPosition = Position * ChunkSize;
        Matrix4 model = Matrix4.CreateTranslation(worldPosition.X, worldPosition.Y, worldPosition.Z);
        _renderer.Render(shader, model, view, projection);

        foreach (var voxelObject in _voxelObjects)
        {
            voxelObject.Render(shader, view, projection);
        }
    }

    public void Dispose()
    {
        foreach (var obj in new List<VoxelObject>(_voxelObjects))
        {
            WorldManager.QueueForRemoval(obj);
        }
        _voxelObjects.Clear();

        Unload();
        Voxels.Clear();
    }
}