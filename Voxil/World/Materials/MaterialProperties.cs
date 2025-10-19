// /World/Materials/MaterialProperties.cs
/// <summary>
/// Хранит неизменяемые свойства для каждого типа материала.
/// </summary>
public readonly struct MaterialProperties
{
    public (float r, float g, float b) Color { get; }
    public float MassPerVoxel { get; }
    // В будущем: public float Density { get; }
    // В будущем: public float Hardness { get; }

    public MaterialProperties((float r, float g, float b) color, float massPerVoxel)
    {
        Color = color;
        MassPerVoxel = massPerVoxel;
    }
}