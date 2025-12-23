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
    public Vector3 Position { get; private set; }
    public Quaternion Rotation { get; private set; }
    public Vector3 LocalBoundsMin { get; private set; }
    public Vector3 LocalBoundsMax { get; private set; }
    
    public event Action<VoxelObject> OnEmpty; 

    private bool _isDisposed = false;

    public VoxelObject(List<Vector3i> voxelCoordinates, MaterialType material) // Убрали WorldManager из конструктора
    {
        VoxelCoordinates = voxelCoordinates ?? throw new ArgumentNullException(nameof(voxelCoordinates));
        Material = material;
    }

    public void InitializePhysics(BodyHandle handle, Vector3 localCenterOfMass)
    {
        BodyHandle = handle;
        LocalCenterOfMass = localCenterOfMass;
        RecalculateBounds();
    }

    public void UpdatePose(RigidPose pose)
    {
        Position = pose.Position.ToOpenTK();
        var orientation = pose.Orientation;
        Rotation = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
    }

    private void RecalculateBounds()
    {
        if (VoxelCoordinates.Count == 0) return;

        var min = new Vector3i(int.MaxValue);
        var max = new Vector3i(int.MinValue);

        foreach (var v in VoxelCoordinates)
        {
            if (v.X < min.X) min.X = v.X;
            if (v.Y < min.Y) min.Y = v.Y;
            if (v.Z < min.Z) min.Z = v.Z;
            if (v.X > max.X) max.X = v.X;
            if (v.Y > max.Y) max.Y = v.Y;
            if (v.Z > max.Z) max.Z = v.Z;
        }

        Vector3 worldMin = new Vector3(min.X, min.Y, min.Z);
        Vector3 worldMax = new Vector3(max.X + 1, max.Y + 1, max.Z + 1);

        LocalBoundsMin = worldMin - LocalCenterOfMass;
        LocalBoundsMax = worldMax - LocalCenterOfMass;
    }

    public void RebuildMeshAndPhysics(PhysicsWorld physicsWorld)
    {
        if (_isDisposed) return;
        if (!physicsWorld.Simulation.Bodies.BodyExists(BodyHandle)) return;

        var oldCoM = LocalCenterOfMass;
        try
        {
            var newHandle = physicsWorld.UpdateVoxelObjectBody(BodyHandle, VoxelCoordinates, Material, out var newCenterOfMassBepu);
            var newCoM = newCenterOfMassBepu.ToOpenTK();

            var shiftLocal = newCoM - oldCoM;
            var bodyRef = physicsWorld.Simulation.Bodies.GetBodyReference(BodyHandle);
            var orientation = bodyRef.Pose.Orientation;
            var tkOrientation = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
            var shiftWorld = Vector3.Transform(shiftLocal, tkOrientation);

            bodyRef.Pose.Position += shiftWorld.ToSystemNumerics();
            bodyRef.Awake = true;

            LocalCenterOfMass = newCoM;
            BodyHandle = newHandle;
            RecalculateBounds();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoxelObject] Error updating: {ex.Message}");
        }
    }

    public bool RemoveVoxel(Vector3i localPos)
    {
        int index = VoxelCoordinates.FindIndex(v => v == localPos);
        if (index != -1)
        {
            VoxelCoordinates.RemoveAt(index);
            if (VoxelCoordinates.Count == 0)
            {
                // Вместо WorldManager.QueueForRemoval(this)
                OnEmpty?.Invoke(this); 
            }
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
    }
}