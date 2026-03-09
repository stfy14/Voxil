// --- Game/Items/GlowBallItem.cs ---
using BepuPhysics;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

/// <summary>
/// Светящийся шар — бросаемый предмет, излучающий свет как point light.
/// Выполняет роль "факела на верёвке": освещает окружение после броска.
/// Ключ: слот 3 инвентаря, кинуть — ЛКМ.
/// </summary>
public class GlowBallItem : ThrowableItem
{
    private readonly IWorldService _worldService;
    private readonly IVoxelObjectService _objService;

    // Форма шара из Glow-вокселей: сфероид 3×3×3 (некоторые угловые вокселя убраны)
    private static readonly List<Vector3i> _glowShape = BuildGlowShape();

    private static List<Vector3i> BuildGlowShape()
    {
        // Компактный крестообразный шарик 3×3×3 без угловых диагоналей
        var shape = new List<Vector3i>();
        for (int x = 0; x < 3; x++)
        for (int y = 0; y < 3; y++)
        for (int z = 0; z < 3; z++)
        {
            // Убираем угловые воксели чтобы было похоже на шар
            int dist = Math.Abs(x - 1) + Math.Abs(y - 1) + Math.Abs(z - 1);
            if (dist <= 2) // Манхэттенское расстояние ≤ 2 → сохраняем
                shape.Add(new Vector3i(x, y, z));
        }
        return shape;
    }

    public GlowBallItem()
    {
        _worldService = ServiceLocator.Get<IWorldService>();
        _objService   = ServiceLocator.Get<IVoxelObjectService>();

        Name       = "GlowBall";
        ThrowForce = 18.0f;
        Cooldown   = 0.6f;

        // ViewModel — светящийся шарик в руке
        var vmMaterials = new System.Collections.Generic.Dictionary<Vector3i, uint>();
        foreach (var v in _glowShape)
            vmMaterials[v] = (uint)MaterialType.Glow;

        ViewModel = new VoxelObject(new List<Vector3i>(_glowShape), MaterialType.Glow, 0.12f, vmMaterials);
    }

    protected override void Throw(Player player)
    {
        var camPos = player.Camera.Position;
        var camDir = player.Camera.Front;

        // Спавним чуть впереди и ниже камеры
        var spawnOffset = camDir * 1.2f + new Vector3(0, -0.2f, 0);
        var spawnPos    = (camPos + spawnOffset).ToSystemNumerics();

        // Скорость = направление камеры × сила броска + скорость игрока
        var playerVelocity = _worldService.GetPlayerVelocity();
        var throwVelocity  = camDir.ToSystemNumerics() * ThrowForce + playerVelocity;

        // Создаём VoxelObject с Glow-материалом
        var materials = new System.Collections.Generic.Dictionary<Vector3i, uint>();
        foreach (var v in _glowShape)
            materials[v] = (uint)MaterialType.Glow;

        var glowBall = new VoxelObject(new List<Vector3i>(_glowShape), MaterialType.Glow, 0.2f, materials);
        _objService.SpawnDynamicObject(glowBall, spawnPos, throwVelocity);

        Console.WriteLine($"[GlowBall] Thrown from {camPos:F1}, velocity={throwVelocity.Length():F1} m/s");
    }
}