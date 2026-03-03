// --- START OF FILE MaterialProperties.cs ---
public readonly struct MaterialProperties
{
    public (float r, float g, float b) Color { get; }
    public float MassPerVoxel { get; }
    public float Hardness { get; } // <--- НОВОЕ ПОЛЕ: Прочность материала

    public MaterialProperties((float r, float g, float b) color, float massPerVoxel, float hardness)
    {
        Color = color;
        MassPerVoxel = massPerVoxel;
        Hardness = hardness;
    }
}