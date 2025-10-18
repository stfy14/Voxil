// Camera.cs
using OpenTK.Mathematics;
using System;

public class Camera
{
    private Vector3 _position;
    private Vector3 _front;
    private Vector3 _up;
    private Vector3 _right;
    private readonly Vector3 _worldUp = Vector3.UnitY;

    private float _yaw = -90.0f;
    private float _pitch = 0.0f;

    private float _fov = 45.0f;
    private float _aspectRatio;
    private const float NearPlane = 0.1f;
    private const float FarPlane = 200.0f;

    public Vector3 Position => _position;
    public Vector3 Front => _front;
    public float Fov => _fov;

    public Camera(Vector3 position, Vector3 target, float aspectRatio)
    {
        _position = position;
        _aspectRatio = aspectRatio;

        // Вычисляем начальные углы поворота из направления к цели
        Vector3 direction = Vector3.Normalize(target - position);
        _pitch = MathHelper.RadiansToDegrees((float)Math.Asin(direction.Y));
        _yaw = MathHelper.RadiansToDegrees((float)Math.Atan2(direction.Z, direction.X));

        UpdateVectors();
    }

    // Новый метод для прямой установки позиции (для Player)
    public void SetPosition(Vector3 position)
    {
        _position = position;
    }

    public Vector3 Right => _right;

    public Matrix4 GetViewMatrix()
    {
        return Matrix4.LookAt(_position, _position + _front, _up);
    }

    public Matrix4 GetProjectionMatrix()
    {
        return Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(_fov),
            _aspectRatio,
            NearPlane,
            FarPlane
        );
    }

    public void MoveForward(float distance)
    {
        _position += _front * distance;
    }

    public void MoveRight(float distance)
    {
        _position += _right * distance;
    }

    public void MoveUp(float distance)
    {
        _position += _up * distance;
    }

    public void Rotate(float deltaYaw, float deltaPitch)
    {
        _yaw += deltaYaw;
        _pitch -= deltaPitch;

        // Ограничиваем pitch, чтобы не переворачивать камеру
        _pitch = Math.Clamp(_pitch, -89.0f, 89.0f);

        UpdateVectors();
    }

    public void UpdateAspectRatio(float aspectRatio)
    {
        _aspectRatio = aspectRatio;
    }

    public void SetFov(float fov)
    {
        _fov = Math.Clamp(fov, 1.0f, 90.0f);
    }

    private void UpdateVectors()
    {
        // Вычисляем новый вектор направления
        Vector3 front;
        front.X = (float)(Math.Cos(MathHelper.DegreesToRadians(_yaw)) * Math.Cos(MathHelper.DegreesToRadians(_pitch)));
        front.Y = (float)Math.Sin(MathHelper.DegreesToRadians(_pitch));
        front.Z = (float)(Math.Sin(MathHelper.DegreesToRadians(_yaw)) * Math.Cos(MathHelper.DegreesToRadians(_pitch)));
        _front = Vector3.Normalize(front);

        // Пересчитываем right и up векторы
        _right = Vector3.Normalize(Vector3.Cross(_front, _worldUp));
        _up = Vector3.Normalize(Vector3.Cross(_right, _front));
    }
}