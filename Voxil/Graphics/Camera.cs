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
    private const float FarPlane = 500.0f;

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