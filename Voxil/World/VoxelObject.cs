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
    public Vector3 Position { get; private set; }
    public Quaternion Rotation { get; private set; }
    
    // Эти свойства теперь будут хранить границы в локальном геометрическом пространстве
    public Vector3 LocalBoundsMin { get; private set; }
    public Vector3 LocalBoundsMax { get; private set; }
    
    public event Action<VoxelObject> OnEmpty; 

    private bool _isDisposed = false;

    public VoxelObject(List<Vector3i> voxelCoordinates, MaterialType material)
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

        // --- ИСПРАВЛЕНИЕ ЗДЕСЬ ---
        // Убираем вычитание LocalCenterOfMass. Границы теперь находятся
        // в том же пространстве, что и VoxelCoordinates (геометрическое пространство объекта).
        LocalBoundsMin = new Vector3(min.X * s, min.Y * s, min.Z * s);
        LocalBoundsMax = new Vector3((max.X + 1) * s, (max.Y + 1) * s, (max.Z + 1) * s);
    }

    public void RebuildMeshAndPhysics(PhysicsWorld physicsWorld)
    {
        if (_isDisposed) return;
        
        if (VoxelCoordinates.Count == 0) return; 

        if (!physicsWorld.Simulation.Bodies.BodyExists(BodyHandle)) return;

        var oldCoM = LocalCenterOfMass;
        try
        {
            var newHandle = physicsWorld.UpdateVoxelObjectBody(BodyHandle, VoxelCoordinates, Material, out var newCenterOfMassBepu);
            var newCoM = newCenterOfMassBepu.ToOpenTK();
            
            var bodyRef = physicsWorld.Simulation.Bodies.GetBodyReference(newHandle);
            
            // Корректируем мировую позицию тела, чтобы объект визуально остался на месте
            var shiftLocal = newCoM - oldCoM;
            var orientation = bodyRef.Pose.Orientation;
            var tkOrientation = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
            var shiftWorld = Vector3.Transform(shiftLocal, tkOrientation);
            
            bodyRef.Pose.Position -= shiftWorld.ToSystemNumerics();
            bodyRef.Awake = true;

            // Обновляем состояние объекта
            BodyHandle = newHandle;
            LocalCenterOfMass = newCoM;
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
                OnEmpty?.Invoke(this); 
            }
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
}