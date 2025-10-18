// Voxel.cs
public enum MaterialType : byte
{
    Air = 0,
    Dirt = 1,
    Stone = 2,
    Wood = 3,
}

public struct Voxel
{
    public MaterialType Type;
    public bool IsActive; // Активен ли воксель (или это воздух)

    // Здесь можно добавить физические свойства
    // public float Mass;
    // public float Density;

    public Voxel(MaterialType type)
    {
        Type = type;
        IsActive = type != MaterialType.Air;
    }

    public static (float r, float g, float b) GetColor(MaterialType type)
    {
        return type switch
        {
            MaterialType.Stone => (0.5f, 0.5f, 0.5f),
            MaterialType.Dirt => (0.55f, 0.27f, 0.07f),
            MaterialType.Wood => (0.4f, 0.26f, 0.13f),
            _ => (1.0f, 0.0f, 1.0f) // Розовый для отладки
        };
    }
}