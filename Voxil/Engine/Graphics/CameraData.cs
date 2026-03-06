// --- Engine/Graphics/CameraData.cs ---
using OpenTK.Mathematics;

public struct CameraData
{
    public Vector3 Position;
    public Matrix4 View;
    public Matrix4 Projection;
    public Matrix4 JitteredProjection;

    // Удобный фабричный метод для FPS камеры
    public static CameraData From(Camera cam, Vector2 jitter = default, float renderWidth = 1, float renderHeight = 1)
    {
        return new CameraData
        {
            Position          = cam.Position,
            View              = cam.GetViewMatrix(),
            Projection        = cam.GetProjectionMatrix(),
            JitteredProjection = cam.GetJitteredProjectionMatrix(jitter, renderWidth, renderHeight)
        };
    }

    // Фабричный метод для орбитальной камеры (TAA не нужен — JitteredProjection = Projection)
    public static CameraData From(OrbitalCamera cam)
    {
        var proj = cam.GetProjectionMatrix();
        return new CameraData
        {
            Position           = cam.Position,
            View               = cam.GetViewMatrix(),
            Projection         = proj,
            JitteredProjection = proj
        };
    }
}