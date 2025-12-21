// /Utils/VectorExtensions.cs
public static class VectorExtensions
{
    public static System.Numerics.Vector3 ToSystemNumerics(this OpenTK.Mathematics.Vector3 vec)
    {
        return new System.Numerics.Vector3(vec.X, vec.Y, vec.Z);
    }

    public static System.Numerics.Vector3 ToSystemNumerics(this OpenTK.Mathematics.Vector3i vec)
    {
        return new System.Numerics.Vector3(vec.X, vec.Y, vec.Z);
    }

    public static OpenTK.Mathematics.Vector3 ToOpenTK(this System.Numerics.Vector3 vec)
    {
        return new OpenTK.Mathematics.Vector3(vec.X, vec.Y, vec.Z);
    }

    public static int LengthSquared(this OpenTK.Mathematics.Vector3i vec)
    {
        return vec.X * vec.X + vec.Y * vec.Y + vec.Z * vec.Z;
    }
}