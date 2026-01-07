using OpenTK.Mathematics;

public static class VectorExtensions
{
    // --- Vector3 ---
    public static System.Numerics.Vector3 ToSystemNumerics(this OpenTK.Mathematics.Vector3 vec)
    {
        return new System.Numerics.Vector3(vec.X, vec.Y, vec.Z);
    }

    public static OpenTK.Mathematics.Vector3 ToOpenTK(this System.Numerics.Vector3 vec)
    {
        return new OpenTK.Mathematics.Vector3(vec.X, vec.Y, vec.Z);
    }

    // --- Vector3i ---
    public static System.Numerics.Vector3 ToSystemNumerics(this OpenTK.Mathematics.Vector3i vec)
    {
        return new System.Numerics.Vector3(vec.X, vec.Y, vec.Z);
    }

    public static OpenTK.Mathematics.Vector3 ToOpenTK(this OpenTK.Mathematics.Vector3i vec)
    {
        return new OpenTK.Mathematics.Vector3(vec.X, vec.Y, vec.Z);
    }

    // --- QUATERNION (НОВОЕ) ---
    public static System.Numerics.Quaternion ToSystemNumerics(this OpenTK.Mathematics.Quaternion q)
    {
        return new System.Numerics.Quaternion(q.X, q.Y, q.Z, q.W);
    }

    public static OpenTK.Mathematics.Quaternion ToOpenTK(this System.Numerics.Quaternion q)
    {
        return new OpenTK.Mathematics.Quaternion(q.X, q.Y, q.Z, q.W);
    }

    public static int LengthSquared(this OpenTK.Mathematics.Vector3i vec)
    {
        return vec.X * vec.X + vec.Y * vec.Y + vec.Z * vec.Z;
    }
}