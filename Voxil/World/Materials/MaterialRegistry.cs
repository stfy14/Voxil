// --- START OF FILE MaterialRegistry.cs ---
using System.Collections.Generic;

public static class MaterialRegistry
{
    private static readonly Dictionary<MaterialType, MaterialProperties> _definitions;

    static MaterialRegistry()
    {
        _definitions = new Dictionary<MaterialType, MaterialProperties>
        {
            // Цвет, Масса, Здоровье(Hardness)
            [MaterialType.Air]   = new MaterialProperties((0, 0, 0), 0f, 0f),
            [MaterialType.Dirt]  = new MaterialProperties((0.55f, 0.27f, 0.07f), 1.3f, 30f),  // Слабый
            [MaterialType.Stone] = new MaterialProperties((0.5f, 0.5f, 0.5f), 2.5f, 100f), // Крепкий
            [MaterialType.Wood]  = new MaterialProperties((0.4f, 0.26f, 0.13f), 0.7f, 50f),
            [MaterialType.Water] = new MaterialProperties((0.2f, 0.4f, 0.8f), 1.0f, 500f), // Гасит взрыв
            [MaterialType.Grass] = new MaterialProperties((0.15f, 0.60f, 0.05f), 1.2f, 30f),
            [MaterialType.TNT]   = new MaterialProperties((0.8f, 0.1f, 0.1f), 1.5f, 10f),  // Очень хрупкий
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
                return true; 
            case MaterialType.Air:
            default:
                return false; 
        }
    }
}