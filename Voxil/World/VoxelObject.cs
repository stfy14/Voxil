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
    
    // ✅ НОВОЕ ПОЛЕ: Якорная точка (минимальный воксель в мировых координатах)
    // Это НЕ меняется при перестройке физики
    public Vector3 AnchorWorldPosition { get; private set; }
    
    // Текущее состояние (позиция физического тела = CoM в мире)
    public Vector3 Position { get; private set; }
    public Quaternion Rotation { get; private set; }

    // Предыдущее состояние (для интерполяции)
    public Vector3 PrevPosition { get; private set; }
    public Quaternion PrevRotation { get; private set; }

    public Vector3 LocalBoundsMin { get; private set; }
    public Vector3 LocalBoundsMax { get; private set; }
    
    public event Action<VoxelObject> OnEmpty; 

    private bool _isDisposed = false;
    private bool _firstUpdate = true;

    public VoxelObject(List<Vector3i> voxelCoordinates, MaterialType material)
    {
        VoxelCoordinates = voxelCoordinates ?? throw new ArgumentNullException(nameof(voxelCoordinates));
        Material = material;
        Rotation = Quaternion.Identity;
        PrevRotation = Quaternion.Identity;
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
    
    // --- ИНТЕРПОЛЯЦИЯ ---
    public Matrix4 GetInterpolatedModelMatrix(float alpha)
    {
        Vector3 interpPos = Vector3.Lerp(PrevPosition, Position, alpha);
        Quaternion interpRot = Quaternion.Slerp(PrevRotation, Rotation, alpha);
        
        // КРИТИЧНО: Model matrix для визуала
        // 1. Translate(-CoM) — сдвиг к началу координат относительно ЛОКАЛЬНЫХ вокселей
        // 2. Rotate — вращение
        // 3. Translate(Position) — в физическую позицию (центр масс)
        return Matrix4.CreateTranslation(-LocalCenterOfMass) * 
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
    
    // ✅ КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: Нормализуем координаты к (0,0,0)
    // Это гарантирует консистентность физики и инверсии Z
    int minX = VoxelCoordinates.Min(v => v.X);
    int minY = VoxelCoordinates.Min(v => v.Y);
    int minZ = VoxelCoordinates.Min(v => v.Z);
    
    Vector3i offset = new Vector3i(minX, minY, minZ);
    
    // Если есть смещение — нормализуем список
    if (offset != Vector3i.Zero)
    {
        for (int i = 0; i < VoxelCoordinates.Count; i++)
        {
            VoxelCoordinates[i] -= offset;
        }
    }
    
    try
    {
        var newHandle = physicsWorld.UpdateVoxelObjectBody(BodyHandle, VoxelCoordinates, Material, out var newCenterOfMassBepu);
        var newCoM = newCenterOfMassBepu.ToOpenTK();
        
        var bodyRef = physicsWorld.Simulation.Bodies.GetBodyReference(newHandle);
        
        // Корректируем позицию с учётом нового CoM И смещения координат
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
            Console.WriteLine($"[VoxelObject] Removing voxel {localPos} at index {index}");
            VoxelCoordinates.RemoveAt(index);
            if (VoxelCoordinates.Count == 0) OnEmpty?.Invoke(this); 
            return true;
        }
        Console.WriteLine($"[VoxelObject] FAILED: Voxel {localPos} not found");
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