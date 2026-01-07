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

        // Индексы в метры
        float s = Constants.VoxelSize;
        Vector3 worldMin = new Vector3(min.X * s, min.Y * s, min.Z * s);
        Vector3 worldMax = new Vector3((max.X + 1) * s, (max.Y + 1) * s, (max.Z + 1) * s);

        LocalBoundsMin = worldMin - LocalCenterOfMass;
        LocalBoundsMax = worldMax - LocalCenterOfMass;
    }

    public void RebuildMeshAndPhysics(PhysicsWorld physicsWorld)
    {
        if (_isDisposed) return;
        
        // --- ФИКС КРАША ---
        // Если вокселей не осталось, не пытаемся строить физику.
        // Событие OnEmpty уже сработало в RemoveVoxel, WorldManager удалит этот объект в следующем кадре.
        if (VoxelCoordinates.Count == 0) return; 

        if (!physicsWorld.Simulation.Bodies.BodyExists(BodyHandle)) return;

        var oldCoM = LocalCenterOfMass;
        try
        {
            var newHandle = physicsWorld.UpdateVoxelObjectBody(BodyHandle, VoxelCoordinates, Material, out var newCenterOfMassBepu);
            var newCoM = newCenterOfMassBepu.ToOpenTK();

            // Корректируем позицию тела, так как центр масс сместился
            var shiftLocal = newCoM - oldCoM;
            var bodyRef = physicsWorld.Simulation.Bodies.GetBodyReference(BodyHandle);
            
            // ВАЖНО: Читаем ориентацию аккуратно
            var orientation = bodyRef.Pose.Orientation;
            var tkOrientation = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
            var shiftWorld = Vector3.Transform(shiftLocal, tkOrientation);

            // Если хендл изменился (старое тело удалено, новое создано)
            // UpdateVoxelObjectBody уже возвращает новый хендл, и он уже в Simulation
            
            // Если UpdateVoxelObjectBody внутри себя делает Remove/Add, то bodyRef выше может быть невалидным, 
            // если мы взяли его от старого Handle.
            // Но PhysicsWorld.UpdateVoxelObjectBody возвращает НОВЫЙ Handle.
            
            // Берем референс от НОВОГО хендла
            var newBodyRef = physicsWorld.Simulation.Bodies.GetBodyReference(newHandle);
            newBodyRef.Pose.Position += shiftWorld.ToSystemNumerics();
            newBodyRef.Awake = true;

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
                OnEmpty?.Invoke(this); 
            }
            return true;
        }
        return false;
    }

    // --- ЛОГИКА РАЗДЕЛЕНИЯ (BFS) ---
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
                
                // Соседи
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