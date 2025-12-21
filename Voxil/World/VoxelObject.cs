// /World/VoxelObject.cs - ПОЛНОСТЬЮ ИСПРАВЛЕН
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
    private bool _isDisposed = false;

    public VoxelObject(List<Vector3i> voxelCoordinates, MaterialType material, WorldManager worldManager)
    {
        VoxelCoordinates = voxelCoordinates ?? throw new ArgumentNullException(nameof(voxelCoordinates));
        Material = material;
        WorldManager = worldManager ?? throw new ArgumentNullException(nameof(worldManager));
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
        if (_isDisposed) return;

        _renderer?.Dispose();

        var voxelsDict = new Dictionary<Vector3i, MaterialType>();
        foreach (var coord in VoxelCoordinates)
        {
            voxelsDict[coord] = Material;
        }

        // --- ИЗМЕНЕНИЯ ЗДЕСЬ ---
        // 1. Создаем списки ПЕРЕД вызовом метода
        var vertices = new List<float>();
        var colors = new List<float>();
        var aoValues = new List<float>();

        // 2. Вызываем метод, передавая ему списки БЕЗ ключевого слова out
        VoxelMeshBuilder.GenerateMesh(voxelsDict,
            vertices, colors, aoValues,
            localPos => voxelsDict.ContainsKey(localPos));
        // -------------------------

        _renderer = new VoxelObjectRenderer(vertices, colors, aoValues);
    }

    /// <summary>
    /// ИСПРАВЛЕНИЕ: Проверяем существование body перед обновлением
    /// </summary>
    public void RebuildMeshAndPhysics(PhysicsWorld physicsWorld)
    {
        if (_isDisposed) return;

        if (!physicsWorld.Simulation.Bodies.BodyExists(BodyHandle))
        {
            Console.WriteLine($"[VoxelObject] Cannot rebuild: body {BodyHandle.Value} does not exist!");
            return;
        }

        // 1. Сохраняем СТАРЫЙ центр масс перед обновлением
        var oldCoM = LocalCenterOfMass;

        // 2. Генерируем новый меш
        var voxelsDict = new Dictionary<Vector3i, MaterialType>();
        foreach (var coord in VoxelCoordinates)
        {
            voxelsDict[coord] = Material;
        }

        var vertices = new List<float>();
        var colors = new List<float>();
        var aoValues = new List<float>();

        VoxelMeshBuilder.GenerateMesh(voxelsDict, vertices, colors, aoValues, localPos => voxelsDict.ContainsKey(localPos));

        _renderer?.UpdateMesh(vertices, colors, aoValues);

        try
        {
            // 3. Обновляем физику и получаем НОВЫЙ центр масс
            var newHandle = physicsWorld.UpdateVoxelObjectBody(BodyHandle, VoxelCoordinates, Material, out var newCenterOfMassBepu);
            var newCoM = newCenterOfMassBepu.ToOpenTK();

            // 4. --- ГЛАВНОЕ ИСПРАВЛЕНИЕ ---
            // Рассчитываем, насколько сдвинулся центр масс в локальных координатах
            var shiftLocal = newCoM - oldCoM;

            // Получаем текущую ориентацию тела
            var bodyRef = physicsWorld.Simulation.Bodies.GetBodyReference(BodyHandle);
            var orientation = bodyRef.Pose.Orientation;
            var tkOrientation = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);

            // Переводим сдвиг в мировые координаты (учитываем поворот объекта)
            var shiftWorld = Vector3.Transform(shiftLocal, tkOrientation);

            // Сдвигаем САМО ТЕЛО на этот вектор.
            // Это компенсирует изменение формы, и блоки останутся на месте визуально и физически.
            bodyRef.Pose.Position += shiftWorld.ToSystemNumerics();

            // Если тело спало, разбудим его, чтобы физика подхватила изменения
            bodyRef.Awake = true;

            // 5. Сохраняем новые данные
            LocalCenterOfMass = newCoM;
            BodyHandle = newHandle;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoxelObject] Error updating physics: {ex.Message}\n{ex.StackTrace}");
        }
    }

    public void Render(Shader shader, Matrix4 view, Matrix4 projection)
    {
        if (_isDisposed || _renderer == null) return;

        // Сначала сдвигаем меш НАЗАД на величину центра масс (-LocalCenterOfMass).
        // Это совмещает визуальный центр масс с точкой (0,0,0).
        // Затем вращаем.
        // Затем переносим в мировую позицию тела.

        Matrix4 model = Matrix4.CreateTranslation(-LocalCenterOfMass) * // <--- Самое важное
                        Matrix4.CreateFromQuaternion(Rotation) *
                        Matrix4.CreateTranslation(Position);

        _renderer.Render(shader, model, view, projection);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _renderer?.Dispose();
        _renderer = null;
    }
}