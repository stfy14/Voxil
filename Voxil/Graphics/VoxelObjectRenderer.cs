// /Graphics/VoxelObjectRenderer.cs
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

/// <summary>
/// Отвечает за рендеринг одного VoxelObject. Управляет ресурсами GPU (VAO, VBO).
/// </summary>
public class VoxelObjectRenderer : IDisposable
{
    private int _vertexArrayObject;
    private int _vertexBufferObject;
    private int _colorBufferObject;
    private int _aoBufferObject;
    private int _vertexCount;
    private bool _disposed;

    public VoxelObjectRenderer(List<float> vertices, List<float> colors, List<float> aoValues)
    {
        UploadMeshToGpu(vertices, colors, aoValues);
    }

    public void Render(Shader shader, Matrix4 model, Matrix4 view, Matrix4 projection)
    {
        if (_vertexCount == 0) return;

        shader.Use();
        shader.SetMatrix4("model", model);
        shader.SetMatrix4("view", view);
        shader.SetMatrix4("projection", projection);

        GL.BindVertexArray(_vertexArrayObject);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _vertexCount);
        GL.BindVertexArray(0);
    }

    public void UpdateMesh(List<float> vertices, List<float> colors, List<float> aoValues)
    {
        // Если старых ресурсов не было, создаем их
        if (_vertexArrayObject == 0)
        {
            UploadMeshToGpu(vertices, colors, aoValues);
            return;
        }

        _vertexCount = vertices.Count / 3;
        if (_vertexCount == 0)
        {
            // Если вершин не осталось, можно очистить ресурсы
            CleanupGpuResources();
            return;
        }

        // Перезаписываем данные в существующих буферах
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), vertices.ToArray(), BufferUsageHint.StaticDraw);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _colorBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, colors.Count * sizeof(float), colors.ToArray(), BufferUsageHint.StaticDraw);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _aoBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, aoValues.Count * sizeof(float), aoValues.ToArray(), BufferUsageHint.StaticDraw);

        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }


    private void UploadMeshToGpu(List<float> vertices, List<float> colors, List<float> aoValues)
    {
        _vertexCount = vertices.Count / 3;
        if (_vertexCount == 0) return;

        _vertexArrayObject = GL.GenVertexArray();
        GL.BindVertexArray(_vertexArrayObject);

        _vertexBufferObject = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), vertices.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 0, 0);
        GL.EnableVertexAttribArray(0);

        _colorBufferObject = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _colorBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, colors.Count * sizeof(float), colors.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 0, 0);
        GL.EnableVertexAttribArray(1);

        _aoBufferObject = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _aoBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, aoValues.Count * sizeof(float), aoValues.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, 0, 0);
        GL.EnableVertexAttribArray(2);

        GL.BindVertexArray(0);
    }

    private void CleanupGpuResources()
    {
        if (_vertexArrayObject != 0) GL.DeleteVertexArray(_vertexArrayObject);
        if (_vertexBufferObject != 0) GL.DeleteBuffer(_vertexBufferObject);
        if (_colorBufferObject != 0) GL.DeleteBuffer(_colorBufferObject);
        if (_aoBufferObject != 0) GL.DeleteBuffer(_aoBufferObject);
        _vertexArrayObject = _vertexBufferObject = _colorBufferObject = _aoBufferObject = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        CleanupGpuResources();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}