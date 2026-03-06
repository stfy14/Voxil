// --- Engine/Graphics/OrbitalCamera.cs ---
using OpenTK.Mathematics;
using System;

public class OrbitalCamera
{
    // Точка вокруг которой вращаемся
    public Vector3 Target { get; set; } = Vector3.Zero;

    // Расстояние от таргета
    public float Distance { get; set; } = 10.0f;

    // Углы орбиты (в радианах)
    public float Yaw   { get; set; } = 0.0f;
    public float Pitch { get; set; }

    // Ограничения
    private const float MinPitch = -1.5533f; // -89° в радианах
    private const float MaxPitch =  1.5533f; // +89° в радианах
    private const float MinDistance = 0.5f;
    private const float MaxDistance = 100.0f;

    // Чувствительность
    public float RotateSensitivity = 0.005f;
    public float PanSensitivity    = 0.01f;
    public float ZoomSensitivity   = 1.0f;

    public float AspectRatio { get; set; } = 1.0f;
    private const float Fov = 60.0f;

    public OrbitalCamera()
    {
        Pitch = MathHelper.DegreesToRadians(30.0f); // поставить сюда
    }
    
    // Позиция камеры вычисляется из орбитальных параметров
    public Vector3 Position
    {
        get
        {
            float x = Distance * MathF.Cos(Pitch) * MathF.Sin(Yaw);
            float y = Distance * MathF.Sin(Pitch);
            float z = Distance * MathF.Cos(Pitch) * MathF.Cos(Yaw);
            return Target + new Vector3(x, y, z);
        }
    }

    public Matrix4 GetViewMatrix()
    {
        return Matrix4.LookAt(Position, Target, Vector3.UnitY);
    }

    public Matrix4 GetProjectionMatrix()
    {
        return Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(Fov),
            AspectRatio, 0.1f, 1000.0f);
    }

    // Вращение орбиты (зажатый ЛКМ или средняя кнопка)
    public void Rotate(float deltaX, float deltaY)
    {
        Yaw   -= deltaX * RotateSensitivity;
        Pitch += deltaY * RotateSensitivity;
        Pitch  = Math.Clamp(Pitch, MinPitch, MaxPitch);
    }

    // Панорамирование (зажатый Shift + средняя кнопка как в Blender)
    public void Pan(float deltaX, float deltaY)
    {
        // Right вектор — перпендикулярен направлению взгляда и мировому up
        var forward = Vector3.Normalize(Target - Position);
        var right   = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up      = Vector3.Normalize(Vector3.Cross(right, forward));

        float scale = Distance * 0.001f;
        Target -= right * deltaX * scale;
        Target += up    * deltaY * scale;
    }

    // Зум (колёсико мыши)
    public void Zoom(float delta)
    {
        Distance -= delta * ZoomSensitivity;
        Distance  = Math.Clamp(Distance, MinDistance, MaxDistance);
    }

    // Сброс камеры к объекту
    public void FocusOn(Vector3 center, float objectSize)
    {
        Target   = center;
        Distance = objectSize * 3.0f;
        Pitch    = MathHelper.DegreesToRadians(25.0f);
        Yaw      = MathHelper.DegreesToRadians(45.0f);
    }
}