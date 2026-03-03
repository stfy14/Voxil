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
    // Материалы per-voxel. Если позиция отсутствует — используется Material (единый).
    public Dictionary<Vector3i, uint> VoxelMaterials { get; } = new();

    // SVO статус для GPU-рендера
    public bool  SvoDirty           { get; set; } = true;   // true = нужна пересборка
    public uint SvoGpuOffset { get; set; } = uint.MaxValue; // 0xFFFFFFFF = "ещё не готово"
    public int SvoGridSize { get; set; } = 0; // 0 = "ещё не готово"
    public float SvoVoxelWorldSize  { get; set; } = 0f;     // Scale * Constants.VoxelSize
    
    
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

// Замени существующий конструктор
    public VoxelObject(List<Vector3i> voxelCoordinates, MaterialType material, float scale = 1.0f, Dictionary<Vector3i, uint> perVoxelMaterials = null)
    {
        VoxelCoordinates = voxelCoordinates ?? throw new ArgumentNullException(nameof(voxelCoordinates));
        Material = material;
        Scale = scale;
        VoxelMaterials = perVoxelMaterials ?? new Dictionary<Vector3i, uint>();

        // Вычисляем центр масс при создании, чтобы он был всегда!
        LocalCenterOfMass = CalculateLocalCenterOfMass();

        Rotation = Quaternion.Identity;
        PrevRotation = Quaternion.Identity;
        RecalculateBounds();
    
        SvoDirty = true;
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

        // ПРАВИЛЬНЫЙ ПОРЯДОК: Scale -> Translate to CoM -> Rotate -> Translate to World
        // В OpenTK умножение идет справа налево, поэтому пишем в обратном порядке.
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
        if (_isDisposed || VoxelCoordinates.Count == 0 || !physicsWorld.Simulation.Bodies.BodyExists(BodyHandle)) return;

        // 1. Запоминаем старую позицию и центр масс
        var oldPose = physicsWorld.GetPose(BodyHandle);
        var oldCoM = LocalCenterOfMass;
        
        // 2. Нормализуем координаты к (0,0,0), если нужно
        int minX = VoxelCoordinates.Min(v => v.X);
        int minY = VoxelCoordinates.Min(v => v.Y);
        int minZ = VoxelCoordinates.Min(v => v.Z);
        Vector3i offset = new Vector3i(minX, minY, minZ);
        if (offset != Vector3i.Zero)
        {
            for (int i = 0; i < VoxelCoordinates.Count; i++)
                VoxelCoordinates[i] -= offset;
            
            // Также смещаем материалы в словаре!
            var newMaterials = new Dictionary<Vector3i, uint>();
            foreach (var kvp in VoxelMaterials)
            {
                newMaterials[kvp.Key - offset] = kvp.Value;
            }
            VoxelMaterials.Clear();
            foreach (var kvp in newMaterials)
            {
                VoxelMaterials[kvp.Key] = kvp.Value;
            }
        }
        
        // 3. Пересоздаем физическое тело и получаем НОВЫЙ центр масс
        var newHandle = physicsWorld.UpdateVoxelObjectBody(BodyHandle, VoxelCoordinates, Material, Scale, out var newCenterOfMassBepu);
        var newCoM = newCenterOfMassBepu.ToOpenTK();
        
        if (!physicsWorld.Simulation.Bodies.BodyExists(newHandle)) return;

        // 4. Компенсируем сдвиг центра масс
        var bodyRef = physicsWorld.Simulation.Bodies.GetBodyReference(newHandle);
        var tkOrientation = bodyRef.Pose.Orientation.ToOpenTK();

        // Смещение из-за сдвига CoM + смещение из-за нормализации сетки
        var totalLocalShift = (newCoM - oldCoM) + (offset.ToOpenTK() * Constants.VoxelSize * Scale);
        
        // Поворачиваем локальный сдвиг в мировое пространство и применяем к позиции тела
        var worldShift = Vector3.Transform(totalLocalShift, tkOrientation);
        bodyRef.Pose.Position += worldShift.ToSystemNumerics();
        
        // 5. Обновляем состояние
        BodyHandle = newHandle;
        LocalCenterOfMass = newCoM;
        RecalculateBounds();
        SvoDirty = true;
        
        Position = bodyRef.Pose.Position.ToOpenTK();
        PrevPosition = Position;
    }

    public bool RemoveVoxel(Vector3i localPos)
    {
        int index = VoxelCoordinates.FindIndex(v => v.X == localPos.X && v.Y == localPos.Y && v.Z == localPos.Z);
        if (index != -1)
        {
            VoxelCoordinates.RemoveAt(index);
            VoxelMaterials.Remove(localPos);   // ← ДОБАВИТЬ: очищаем per-voxel материал
            SvoDirty = true;                   // ← ДОБАВИТЬ: триггер пересборки SVO
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
    
    // Добавь этот новый приватный метод в конец класса VoxelObject
    private Vector3 CalculateLocalCenterOfMass()
    {
        if (VoxelCoordinates.Count == 0) return Vector3.Zero;

        Vector3 sum = Vector3.Zero;
        foreach (var v in VoxelCoordinates)
        {
            // Берем центр каждого вокселя
            sum += v.ToOpenTK() + new Vector3(0.5f);
        }

        // Усредняем и умножаем на размер вокселя, но НЕ на масштаб (масштаб применяется в матрице)
        return (sum / VoxelCoordinates.Count) * Constants.VoxelSize;
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