// /Graphics/Crosshair.cs
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;

public class Crosshair : IDisposable
{
    private int _vao;
    private int _vbo;
    private Shader _shader;

    private int _screenWidth;
    private int _screenHeight;

    // Настройки прицела
    private const float Size = 20.0f;      // Длина линий
    private const float Thickness = 2.0f;  // Толщина линий
    private readonly Vector4 Color = new Vector4(1.0f, 1.0f, 1.0f, 0.8f); // Белый, полупрозрачный

    public Crosshair(int width, int height)
    {
        _screenWidth = width;
        _screenHeight = height;

        // Простой шейдер для отрисовки сплошного цвета
        string vertSource = @"
            #version 330 core
            layout (location = 0) in vec2 aPos;
            uniform mat4 projection;
            void main() {
                gl_Position = projection * vec4(aPos, 0.0, 1.0);
            }";

        string fragSource = @"
            #version 330 core
            out vec4 FragColor;
            uniform vec4 uColor;
            void main() {
                FragColor = uColor;
            }";

        _shader = new Shader(vertSource, fragSource, isSourceCode: true);

        // Инициализация буферов
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);

        // x, y (2 floats)
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);

        UpdateMesh();
    }

    public void UpdateSize(int width, int height)
    {
        _screenWidth = width;
        _screenHeight = height;
        UpdateMesh();
    }

    private void UpdateMesh()
    {
        float centerX = _screenWidth / 2.0f;
        float centerY = _screenHeight / 2.0f;
        float halfSize = Size / 2.0f;
        float halfThick = Thickness / 2.0f;

        // Генерируем 2 прямоугольника (горизонтальный и вертикальный) = 12 вершин (4 треугольника)
        // Координаты экрана (0,0 - верхний левый угол, но в OpenGL по умолчанию Y вверх, 
        // однако мы настроим матрицу как в DebugOverlay)

        float[] vertices = {
            // Горизонтальная линия
            centerX - halfSize, centerY - halfThick, // Top-Left
            centerX + halfSize, centerY - halfThick, // Top-Right
            centerX + halfSize, centerY + halfThick, // Bottom-Right

            centerX + halfSize, centerY + halfThick, // Bottom-Right
            centerX - halfSize, centerY + halfThick, // Bottom-Left
            centerX - halfSize, centerY - halfThick, // Top-Left

            // Вертикальная линия
            centerX - halfThick, centerY - halfSize,
            centerX + halfThick, centerY - halfSize,
            centerX + halfThick, centerY + halfSize,

            centerX + halfThick, centerY + halfSize,
            centerX - halfThick, centerY + halfSize,
            centerX - halfThick, centerY - halfSize,
        };

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    public void Render()
    {
        // Отключаем тест глубины, чтобы прицел всегда был поверх вокселей
        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _shader.Use();

        // Ортогональная проекция (0,0 - левый верхний угол)
        var projection = Matrix4.CreateOrthographicOffCenter(0.0f, _screenWidth, _screenHeight, 0.0f, -1.0f, 1.0f);
        _shader.SetMatrix4("projection", projection);
        _shader.SetVector4("uColor", Color);

        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 12);
        GL.BindVertexArray(0);

        // Возвращаем тест глубины (важно для остального рендеринга)
        GL.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        _shader.Dispose();
    }

    // Внутренний класс шейдера (адаптированный, чтобы принимать код строки, а не файл)
    private class Shader : IDisposable
    {
        public int Handle;
        public Shader(string vert, string frag, bool isSourceCode = true)
        {
            int vs = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vs, vert);
            GL.CompileShader(vs);
            CheckCompile(vs);

            int fs = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fs, frag);
            GL.CompileShader(fs);
            CheckCompile(fs);

            Handle = GL.CreateProgram();
            GL.AttachShader(Handle, vs);
            GL.AttachShader(Handle, fs);
            GL.LinkProgram(Handle);

            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
        }

        private void CheckCompile(int shader)
        {
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);
            if (success == 0) throw new Exception(GL.GetShaderInfoLog(shader));
        }

        public void Use() => GL.UseProgram(Handle);
        public void SetMatrix4(string n, Matrix4 m) => GL.UniformMatrix4(GL.GetUniformLocation(Handle, n), false, ref m);
        public void SetVector4(string n, Vector4 v) => GL.Uniform4(GL.GetUniformLocation(Handle, n), v);
        public void Dispose() => GL.DeleteProgram(Handle);
    }
}