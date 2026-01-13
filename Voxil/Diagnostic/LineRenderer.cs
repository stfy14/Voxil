using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

public class LineRenderer : IDisposable
{
    private readonly int _vao;
    private readonly int _vbo;
    private readonly Shader _shader;
    private readonly List<float> _vertices = new List<float>();

    public LineRenderer()
    {
        string vert = @"
            #version 330 core
            layout (location = 0) in vec3 aPos;
            layout (location = 1) in vec3 aColor;
            out vec3 vColor;
            uniform mat4 uView;
            uniform mat4 uProjection;
            void main() {
                gl_Position = uProjection * uView * vec4(aPos, 1.0);
                vColor = aColor;
            }";

        string frag = @"
            #version 330 core
            in vec3 vColor;
            out vec4 FragColor;
            void main() {
                FragColor = vec4(vColor, 1.0);
            }";

        _shader = new Shader(vert, frag, true);

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);

        // Pos(3) + Color(3) = 6 floats
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);

        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

        GL.BindVertexArray(0);
    }

    public void DrawLine(Vector3 start, Vector3 end, Vector3 color)
    {
        _vertices.Add(start.X); _vertices.Add(start.Y); _vertices.Add(start.Z);
        _vertices.Add(color.X); _vertices.Add(color.Y); _vertices.Add(color.Z);

        _vertices.Add(end.X); _vertices.Add(end.Y); _vertices.Add(end.Z);
        _vertices.Add(color.X); _vertices.Add(color.Y); _vertices.Add(color.Z);
    }

    public void DrawBox(Vector3 min, Vector3 max, Vector3 color)
    {
        // Нижняя грань
        DrawLine(new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z), color);
        DrawLine(new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, min.Y, max.Z), color);
        DrawLine(new Vector3(max.X, min.Y, max.Z), new Vector3(min.X, min.Y, max.Z), color);
        DrawLine(new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, min.Y, min.Z), color);

        // Верхняя грань
        DrawLine(new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z), color);
        DrawLine(new Vector3(max.X, max.Y, min.Z), new Vector3(max.X, max.Y, max.Z), color);
        DrawLine(new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z), color);
        DrawLine(new Vector3(min.X, max.Y, max.Z), new Vector3(min.X, max.Y, min.Z), color);

        // Вертикальные стойки
        DrawLine(new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z), color);
        DrawLine(new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, max.Y, min.Z), color);
        DrawLine(new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, max.Z), color);
        DrawLine(new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, max.Y, max.Z), color);
    }

    public void DrawPoint(Vector3 pos, float size, Vector3 color)
    {
        DrawLine(pos - Vector3.UnitX * size, pos + Vector3.UnitX * size, color);
        DrawLine(pos - Vector3.UnitY * size, pos + Vector3.UnitY * size, color);
        DrawLine(pos - Vector3.UnitZ * size, pos + Vector3.UnitZ * size, color);
    }

    // ИЗМЕНЕНИЕ: Добавлен параметр enableDepthTest
    public void Render(Camera camera, bool enableDepthTest = true)
    {
        if (_vertices.Count == 0) return;

        // Настройка теста глубины
        if (enableDepthTest)
            GL.Enable(EnableCap.DepthTest);
        else
            GL.Disable(EnableCap.DepthTest);

        _shader.Use();
        _shader.SetMatrix4("uView", camera.GetViewMatrix());
        _shader.SetMatrix4("uProjection", camera.GetProjectionMatrix());

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _vertices.Count * sizeof(float), _vertices.ToArray(), BufferUsageHint.DynamicDraw);

        GL.DrawArrays(PrimitiveType.Lines, 0, _vertices.Count / 6);

        GL.BindVertexArray(0);
        _vertices.Clear(); // Очищаем буфер после отрисовки

        // Возвращаем дефолтное состояние (обычно DepthTest включен)
        GL.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        _shader.Dispose();
    }
}