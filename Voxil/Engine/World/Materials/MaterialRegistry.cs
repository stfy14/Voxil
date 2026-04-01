// --- Engine/World/Materials/MaterialRegistry.cs ---
using System.Collections.Generic;

public static class MaterialRegistry
{
    private static readonly Dictionary<MaterialType, MaterialProperties> _definitions;

    static MaterialRegistry()
    {
        _definitions = new Dictionary<MaterialType, MaterialProperties>
        {
            // Цвет, Масса, Здоровье(Hardness)
            [MaterialType.Air]   = new MaterialProperties((0, 0, 0),       0f,   0f),
            [MaterialType.Dirt]  = new MaterialProperties((0.55f, 0.27f, 0.07f), 1.3f, 30f),
            [MaterialType.Stone] = new MaterialProperties((0.5f, 0.5f, 0.5f),   2.5f, 100f),
            [MaterialType.Wood]  = new MaterialProperties((0.4f, 0.26f, 0.13f), 0.7f, 50f),
            [MaterialType.Water] = new MaterialProperties((0.2f, 0.4f, 0.8f),   1.0f, 500f),
            [MaterialType.Grass] = new MaterialProperties((0.15f, 0.60f, 0.05f),1.2f, 30f),
            [MaterialType.TNT]   = new MaterialProperties((0.8f, 0.1f, 0.1f),   1.5f, 10f),
            // Glow: яркий тёплый жёлто-белый, лёгкий, хрупкий, светится
            [MaterialType.Glow]  = new MaterialProperties((0.97f, 0.82f, 0.20f),0.3f, 5f),
        };
    }

    public static MaterialProperties Get(MaterialType type)
    {
        return _definitions.TryGetValue(type, out var props) ? props : _definitions[MaterialType.Air];
    }

    public static (float r, float g, float b) GetColor(MaterialType type)
    {
        return _definitions.TryGetValue(type, out var props) ? props.Color : (1.0f, 0.0f, 1.0f);
    }

    /// <summary>
    /// Возвращает true для материалов, которые излучают свет (используется renderer'ом для point lights).
    /// </summary>
    public static bool IsEmissive(MaterialType type)
    {
        return type == MaterialType.Glow;
    }

    /// <summary>
    /// Интенсивность point light для данного материала (0 = не светится).
    /// </summary>
    public static float GetEmissiveIntensity(MaterialType type)
    {
        return type switch
        {
            MaterialType.Glow => 3.0f,
            _                 => 0f,
        };
    }

    /// <summary>
    /// Радиус влияния point light для данного материала.
    /// </summary>
    public static float GetEmissiveRadius(MaterialType type)
    {
        return type switch
        {
            MaterialType.Glow => 12.0f,
            _                 => 0f,
        };
    }

    public static bool IsSolidForPhysics(MaterialType type)
    {
        switch (type)
        {
            case MaterialType.Dirt:
            case MaterialType.Stone:
            case MaterialType.Wood:
            case MaterialType.Water:
            case MaterialType.Grass:
            case MaterialType.TNT:
            case MaterialType.Glow:
                return true;
            case MaterialType.Air:
            default:
                return false;
        }
    }
}