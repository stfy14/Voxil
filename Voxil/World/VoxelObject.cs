// /World/VoxelObject.cs
using BepuPhysics;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

public class VoxelObject : IDisposable
{
    public List<Vector3i> VoxelCoordinates { get; }
    public MaterialType Material { get; }
    public BodyHandle BodyHandle { get; private set; }
    public Vector3 LocalCenterOfMass { get; private set; }
    public WorldManager WorldManager { get; }

    public Vector3 Position { get; private set; }
    public Quaternion Rotation { get; private set; }

    private VoxelObjectRenderer _renderer;

    public VoxelObject(List<Vector3i> voxelCoordinates, MaterialType material, WorldManager worldManager)
    {
        VoxelCoordinates = voxelCoordinates;
        Material = material;
        WorldManager = worldManager;
    }

    public void InitializePhysics(BodyHandle handle, Vector3 localCenterOfMass)
    {
        BodyHandle = handle;
        LocalCenterOfMass = localCenterOfMass;
    }

    public void UpdatePose(RigidPose pose)
    {
        Position = pose.Position.ToOpenTK();
        var orientation = pose.Orientation;
        Rotation = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
    }

    public void BuildMesh()
    {
        _renderer?.Dispose();

        var voxelsDict = new Dictionary<Vector3i, MaterialType>();
        foreach (var coord in VoxelCoordinates)
        {
            voxelsDict[coord] = this.Material;
        }

        VoxelMeshBuilder.GenerateMesh(voxelsDict,
            out var vertices, out var colors, out var aoValues);

        _renderer = new VoxelObjectRenderer(vertices, colors, aoValues);
    }

    public void RebuildMeshAndPhysics(PhysicsWorld physicsWorld)
    {
        var voxelsDict = new Dictionary<Vector3i, MaterialType>();
        foreach (var coord in VoxelCoordinates)
        {
            voxelsDict[coord] = this.Material;
        }

        VoxelMeshBuilder.GenerateMesh(voxelsDict,
            out var vertices, out var colors, out var aoValues);

        _renderer.UpdateMesh(vertices, colors, aoValues);

        var newHandle = physicsWorld.UpdateVoxelObjectBody(BodyHandle, VoxelCoordinates, Material, out var newCenterOfMass);
        this.LocalCenterOfMass = newCenterOfMass.ToOpenTK();
    }


    public void Render(Shader shader, Matrix4 view, Matrix4 projection)
    {
        if (_renderer == null) return;

        Matrix4 model = Matrix4.CreateTranslation(-LocalCenterOfMass) *
                        Matrix4.CreateFromQuaternion(Rotation) *
                        Matrix4.CreateTranslation(Position);

        _renderer.Render(shader, model, view, projection);
    }

    public void Dispose()
    {
        _renderer?.Dispose();
    }
}