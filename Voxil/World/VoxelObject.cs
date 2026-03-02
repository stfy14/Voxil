// --- START OF FILE VoxelObject.cs ---
using BepuPhysics;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;

public class VoxelObject : IDisposable
{
    public List<Vector3i> VoxelCoordinates { get; }
    public MaterialType Material { get; }
    
    public BodyHandle BodyHandle { get; private set; }
    public Vector3 LocalCenterOfMass { get; private set; }
    public Vector3 AnchorWorldPosition { get; private set; }
    
    public Vector3 Position { get; private set; }
    public Quaternion Rotation { get; private set; }

    public Vector3 PrevPosition { get; private set; }
    public Quaternion PrevRotation { get; private set; }

    public Vector3 LocalBoundsMin { get; private set; }
    public Vector3 LocalBoundsMax { get; private set; }
    
    public event Action<VoxelObject> OnEmpty;
    
    public float Scale { get; } 

    private bool _isDisposed = false;
    private bool _firstUpdate = true;

    public VoxelObject(List<Vector3i> voxelCoordinates, MaterialType material, float scale = 1.0f)
    {
        VoxelCoordinates = voxelCoordinates ?? throw new ArgumentNullException(nameof(voxelCoordinates));
        Material = material;
        Scale = scale; // <--- Сохраняем масштаб

        Rotation = Quaternion.Identity;
        PrevRotation = Quaternion.Identity;
        RecalculateBounds();
    }
    
    public void InitializePhysics(BodyHandle handle, Vector3 localCenterOfMass, Vector3 anchorWorldPos)
    {
        BodyHandle = handle;
        LocalCenterOfMass = localCenterOfMass;
        AnchorWorldPosition = anchorWorldPos;
        RecalculateBounds();
    }

    public void InitializePhysics(BodyHandle handle, Vector3 localCenterOfMass)
    {
        BodyHandle = handle;
        LocalCenterOfMass = localCenterOfMass;
        RecalculateBounds();
    }

    public void UpdatePose(RigidPose pose)
    {
        if (_firstUpdate)
        {
            PrevPosition = pose.Position.ToOpenTK();
            var o = pose.Orientation;
            PrevRotation = new Quaternion(o.X, o.Y, o.Z, o.W);
            _firstUpdate = false;
        }
        else
        {
            PrevPosition = Position;
            PrevRotation = Rotation;
        }

        Position = pose.Position.ToOpenTK();
        var orientation = pose.Orientation;
        Rotation = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
    }
    
    public Matrix4 GetInterpolatedModelMatrix(float alpha)
    {
        Vector3 interpPos = Vector3.Lerp(PrevPosition, Position, alpha);
        Quaternion interpRot = Quaternion.Slerp(PrevRotation, Rotation, alpha);
    
        // ДОБАВЛЕНО: Matrix4.CreateScale(Scale)
        // Порядок важен: Сначала Скейл, потом Сдвиг в центр масс, потом Вращение, потом Позиция
        return Matrix4.CreateScale(Scale) * 
               Matrix4.CreateTranslation(-LocalCenterOfMass) * 
               Matrix4.CreateFromQuaternion(interpRot) * 
               Matrix4.CreateTranslation(interpPos);
    }

    private void RecalculateBounds()
    {
        if (VoxelCoordinates.Count == 0)
        {
            LocalBoundsMin = Vector3.Zero;
            LocalBoundsMax = Vector3.Zero;
            return;
        }

        var min = new Vector3i(int.MaxValue);
        var max = new Vector3i(int.MinValue);

        foreach (var v in VoxelCoordinates)
        {
            min.X = Math.Min(min.X, v.X);
            min.Y = Math.Min(min.Y, v.Y);
            min.Z = Math.Min(min.Z, v.Z);
            max.X = Math.Max(max.X, v.X);
            max.Y = Math.Max(max.Y, v.Y);
            max.Z = Math.Max(max.Z, v.Z);
        }

        float s = Constants.VoxelSize;
        LocalBoundsMin = new Vector3(min.X * s, min.Y * s, min.Z * s);
        LocalBoundsMax = new Vector3((max.X + 1) * s, (max.Y + 1) * s, (max.Z + 1) * s);
    }

    public void RebuildMeshAndPhysics(PhysicsWorld physicsWorld)
    {
        if (_isDisposed) return;
        if (VoxelCoordinates.Count == 0) return; 
        if (!physicsWorld.Simulation.Bodies.BodyExists(BodyHandle)) return;

        var oldCoM = LocalCenterOfMass;
        
        int minX = VoxelCoordinates.Min(v => v.X);
        int minY = VoxelCoordinates.Min(v => v.Y);
        int minZ = VoxelCoordinates.Min(v => v.Z);
        
        Vector3i offset = new Vector3i(minX, minY, minZ);
        
        if (offset != Vector3i.Zero)
        {
            for (int i = 0; i < VoxelCoordinates.Count; i++)
                VoxelCoordinates[i] -= offset;
        }
        
        try
        {
            var newHandle = physicsWorld.UpdateVoxelObjectBody(BodyHandle, VoxelCoordinates, Material, out var newCenterOfMassBepu);
            var newCoM = newCenterOfMassBepu.ToOpenTK();
            
            var bodyRef = physicsWorld.Simulation.Bodies.GetBodyReference(newHandle);
            
            var shiftLocal = (newCoM - oldCoM) + (offset.ToOpenTK() * Constants.VoxelSize);
            var orientation = bodyRef.Pose.Orientation;
            var tkOrientation = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
            var shiftWorld = Vector3.Transform(shiftLocal, tkOrientation);
            
            bodyRef.Pose.Position -= shiftWorld.ToSystemNumerics();
            bodyRef.Awake = true;

            BodyHandle = newHandle;
            LocalCenterOfMass = newCoM;
            RecalculateBounds();
            
            Position = bodyRef.Pose.Position.ToOpenTK();
            PrevPosition = Position;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoxelObject] Error updating: {ex.Message}");
        }
    }

    public bool RemoveVoxel(Vector3i localPos)
    {
        int index = VoxelCoordinates.FindIndex(v => v.X == localPos.X && v.Y == localPos.Y && v.Z == localPos.Z);
        if (index != -1)
        {
            VoxelCoordinates.RemoveAt(index);
            if (VoxelCoordinates.Count == 0) OnEmpty?.Invoke(this); 
            return true;
        }
        return false;
    }

    public List<List<Vector3i>> GetConnectedClusters()
    {
        var clusters = new List<List<Vector3i>>();
        var unvisited = new HashSet<Vector3i>(VoxelCoordinates);
        while (unvisited.Count > 0)
        {
            var root = unvisited.First();
            var cluster = new List<Vector3i>();
            var queue = new Queue<Vector3i>();
            queue.Enqueue(root);
            unvisited.Remove(root);
            cluster.Add(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var neighbors = new Vector3i[] 
                {
                    current + new Vector3i(1,0,0), current + new Vector3i(-1,0,0),
                    current + new Vector3i(0,1,0), current + new Vector3i(0,-1,0),
                    current + new Vector3i(0,0,1), current + new Vector3i(0,0,-1)
                };
                foreach (var n in neighbors)
                {
                    if (unvisited.Contains(n))
                    {
                        unvisited.Remove(n);
                        cluster.Add(n);
                        queue.Enqueue(n);
                    }
                }
            }
            clusters.Add(cluster);
        }
        return clusters;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
    }

    // === Метод для вьюмодели ===
    public void ForceSetTransform(Vector3 pos, Quaternion rot)
    {
        PrevPosition = Position; 
        PrevRotation = Rotation;
        Position = pos;
        Rotation = rot;
    }
}