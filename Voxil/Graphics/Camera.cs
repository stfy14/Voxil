// /Graphics/Camera.cs
using OpenTK.Mathematics;
using System;

public class Camera
{
    private Vector3 _position;
    private Vector3 _front = -Vector3.UnitZ;
    private Vector3 _up = Vector3.UnitY;
    private Vector3 _right = Vector3.UnitX;
    private readonly Vector3 _worldUp = Vector3.UnitY;

    private float _yaw = -90.0f;
    private float _pitch = 0.0f;
    private float _fov = 70.0f;
    private float _aspectRatio;
    private const float NearPlane = 0.01f;
    private const float FarPlane = 8000.0f;

    public Vector3 Position => _position;
    public Vector3 Front => _front;
    public Vector3 Right => _right;
    public float Fov => _fov;

    public Camera(Vector3 position, float aspectRatio)
    {
        _position = position;
        _aspectRatio = aspectRatio;
        UpdateVectors();
    }

    public void SetPosition(Vector3 position)
    {
        _position = position;
    }

    public Matrix4 GetViewMatrix()
    {
        return Matrix4.LookAt(_position, _position + _front, _up);
    }

    // Сдвигает матрицу проекции на(jitter.X, jitter.Y) пикселей
    public Matrix4 GetJitteredProjectionMatrix(Vector2 jitter, float renderWidth, float renderHeight)
    {
        // 1. Берем чистую проекцию
        Matrix4 projection = GetProjectionMatrix();

        // 2. Вычисляем смещение в NDC пространстве (от -1 до 1)
        // jitter - смещение в пикселях (например, 0.5)
        // Делим на ширину/высоту, умножаем на 2 (так как NDC это 2.0 единицы шириной)
        float translationX = (jitter.X / renderWidth) * 2.0f;
        float translationY = (jitter.Y / renderHeight) * 2.0f;

        // 3. Модифицируем компоненты матрицы напрямую (M31 и M32 отвечают за сдвиг в проекции)
        // Это эквивалентно умножению на Matrix4.CreateTranslation, но быстрее
        projection.M31 += translationX;
        projection.M32 += translationY;

        return projection;
    }

    public Matrix4 GetProjectionMatrix()
    {
        return Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(_fov), _aspectRatio, NearPlane, FarPlane);
    }

    public void Rotate(float deltaYaw, float deltaPitch)
    {
        _yaw += deltaYaw;
        _pitch -= deltaPitch;
        _pitch = Math.Clamp(_pitch, -89.0f, 89.0f);
        UpdateVectors();
    }

    public void UpdateAspectRatio(float aspectRatio)
    {
        _aspectRatio = aspectRatio;
    }

    private void UpdateVectors()
    {
        Vector3 front;
        front.X = (float)(Math.Cos(MathHelper.DegreesToRadians(_yaw)) * Math.Cos(MathHelper.DegreesToRadians(_pitch)));
        front.Y = (float)Math.Sin(MathHelper.DegreesToRadians(_pitch));
        front.Z = (float)(Math.Sin(MathHelper.DegreesToRadians(_yaw)) * Math.Cos(MathHelper.DegreesToRadians(_pitch)));
        _front = Vector3.Normalize(front);

        _right = Vector3.Normalize(Vector3.Cross(_front, _worldUp));
        _up = Vector3.Normalize(Vector3.Cross(_right, _front));
    }
}