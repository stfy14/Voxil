// /Utils/VectorExtensions.cs
using OpenTK.Mathematics;

public static class VectorExtensions
{
    // OpenTK Vector3 -> System.Numerics Vector3
    public static System.Numerics.Vector3 ToSystemNumerics(this OpenTK.Mathematics.Vector3 vec)
    {
        return new System.Numerics.Vector3(vec.X, vec.Y, vec.Z);
    }

    // OpenTK Vector3i -> System.Numerics Vector3
    public static System.Numerics.Vector3 ToSystemNumerics(this OpenTK.Mathematics.Vector3i vec)
    {
        return new System.Numerics.Vector3(vec.X, vec.Y, vec.Z);
    }

    // System.Numerics Vector3 -> OpenTK Vector3
    public static OpenTK.Mathematics.Vector3 ToOpenTK(this System.Numerics.Vector3 vec)
    {
        return new OpenTK.Mathematics.Vector3(vec.X, vec.Y, vec.Z);
    }

    // --- ДОБАВЛЕННЫЙ МЕТОД ---
    // OpenTK Vector3i -> OpenTK Vector3
    public static OpenTK.Mathematics.Vector3 ToOpenTK(this OpenTK.Mathematics.Vector3i vec)
    {
        return new OpenTK.Mathematics.Vector3(vec.X, vec.Y, vec.Z);
    }
    // --------------------------

    public static int LengthSquared(this OpenTK.Mathematics.Vector3i vec)
    {
        return vec.X * vec.X + vec.Y * vec.Y + vec.Z * vec.Z;
    }
}