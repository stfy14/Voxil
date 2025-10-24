// /Graphics/DebugOverlay.cs - Финальная версия с независимыми отступами по осям
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.IO;
using StbTrueTypeSharp;
using PixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

public class DebugOverlay : IDisposable
{
    // --- Настройки внешнего вида ---
    private const float Padding = 8.0f;          // Внутренний отступ текста от краев фона
    private const float MarginX = 10.0f;         // Внешний отступ фона от ЛЕВОГО края экрана
    private const float MarginY = 48.0f;         // Внешний отступ фона от ВЕРХНЕГО края экрана
    private readonly Vector4 BackgroundColor = new(0.1f, 0.1f, 0.1f, 0.7f); // Цвет фона (RGBA)
    private readonly Vector4 TextColor = new(1.0f, 1.0f, 1.0f, 1.0f);       // Цвет текста (RGBA)

    private readonly int _vao;
    private readonly int _vbo;
    private readonly int _fontTexture;
    private readonly int _whiteTexture; // Текстура 1x1 для отрисовки сплошным цветом
    private readonly Shader _shader;

    private int _screenWidth;
    private int _screenHeight;

    private readonly float _fontSize;
    private readonly float _lineSpacing;

    private readonly StbTrueType.stbtt_bakedchar[] _bakedChars;
    private const int FirstChar = 32;
    private const int NumChars = 95;

    private float[] _vertices = new float[2048];
    private bool _disposedValue;

    public DebugOverlay(int screenWidth, int screenHeight, float fontSize = 16.0f)
    {
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _fontSize = fontSize;
        _lineSpacing = _fontSize * 1.2f;

        _bakedChars = new StbTrueType.stbtt_bakedchar[NumChars];

        #region Shader Source
        var vertShaderSource = @"
            #version 330 core
            layout (location = 0) in vec4 vertex; // vec2 pos, vec2 uv
            out vec2 TexCoords;
            uniform mat4 projection;
            void main()
            {
                gl_Position = projection * vec4(vertex.xy, 0.0, 1.0);
                TexCoords = vertex.zw;
            }";
        var fragShaderSource = @"
            #version 330 core
            in vec2 TexCoords;
            out vec4 color;
            uniform sampler2D text_sampler;
            uniform vec4 overlayColor;
            void main()
            {
                float alpha = texture(text_sampler, TexCoords).r;
                color = vec4(overlayColor.rgb, overlayColor.a * alpha);
            }";
        #endregion
        _shader = new Shader(vertShaderSource, fragShaderSource);

        _fontTexture = CreateFontTextureAtlas("Assets/RobotoMono-Regular.ttf", 1024, 1024);
        _whiteTexture = CreateWhiteTexture();

        _vbo = GL.GenBuffer();
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.VertexAttribPointer(0, 4, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);
    }

    private int CreateWhiteTexture()
    {
        var textureId = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, textureId);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, 1, 1, 0, PixelFormat.Red, PixelType.UnsignedByte, new byte[] { 0xFF });
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return textureId;
    }

    private int CreateFontTextureAtlas(string fontPath, int atlasWidth, int atlasHeight)
    {
        var fontData = File.ReadAllBytes(fontPath);
        var bitmap = new byte[atlasWidth * atlasHeight];

        StbTrueType.stbtt_BakeFontBitmap(fontData, 0, _fontSize, bitmap, atlasWidth, atlasHeight, FirstChar, _bakedChars.Length, _bakedChars);

        var textureId = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, textureId);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, atlasWidth, atlasHeight, 0, PixelFormat.Red, PixelType.UnsignedByte, bitmap);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return textureId;
    }

    public void UpdateScreenSize(int width, int height)
    {
        _screenWidth = width;
        _screenHeight = height;
    }

    public unsafe void Render(List<string> lines)
    {
        if (lines == null || lines.Count == 0) return;

        // --- 1. Подготовка и расчет размеров ---
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);

        _shader.Use();
        _shader.SetInt("text_sampler", 0);
        var projection = Matrix4.CreateOrthographicOffCenter(0.0f, _screenWidth, _screenHeight, 0.0f, -1.0f, 1.0f);
        _shader.SetMatrix4("projection", projection);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);

        float maxWidth = 0;
        foreach (var line in lines)
        {
            var lineWidth = MeasureString(line);
            if (lineWidth > maxWidth)
                maxWidth = lineWidth;
        }

        float bgWidth = maxWidth + Padding * 2;
        float bgHeight = lines.Count * _lineSpacing + Padding * 2 - (_lineSpacing - _fontSize);

        // --- 2. Отрисовка фона ---
        GL.BindTexture(TextureTarget.Texture2D, _whiteTexture);
        _shader.SetVector4("overlayColor", BackgroundColor);

        // Используем раздельные MarginX и MarginY
        var bg_x0 = MarginX;
        var bg_y0 = MarginY;
        var bg_x1 = MarginX + bgWidth;
        var bg_y1 = MarginY + bgHeight;

        float[] backgroundVertices = {
            bg_x0, bg_y0, 0, 0,   bg_x1, bg_y1, 1, 1,   bg_x0, bg_y1, 0, 1,
            bg_x0, bg_y0, 0, 0,   bg_x1, bg_y0, 1, 0,   bg_x1, bg_y1, 1, 1
        };
        GL.BufferData(BufferTarget.ArrayBuffer, backgroundVertices.Length * sizeof(float), backgroundVertices, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);

        // --- 3. Отрисовка текста ---
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        _shader.SetVector4("overlayColor", TextColor);

        // Используем раздельные MarginX и MarginY для позиционирования текста
        float textX = MarginX + Padding;
        float textY = MarginY + Padding;
        int vertexCount = 0;

        fixed (StbTrueType.stbtt_bakedchar* pBakedChars = _bakedChars)
        {
            foreach (var line in lines)
            {
                float currentX = textX;
                float currentY = textY + _fontSize;

                foreach (char c in line)
                {
                    if (c >= FirstChar && c < FirstChar + NumChars)
                    {
                        var q = new StbTrueType.stbtt_aligned_quad();
                        StbTrueType.stbtt_GetBakedQuad(pBakedChars, 1024, 1024, c - FirstChar, &currentX, &currentY, &q, 1);

                        if (vertexCount + 24 > _vertices.Length) Array.Resize(ref _vertices, _vertices.Length * 2);

                        int offset = vertexCount;
                        _vertices[offset++] = q.x0; _vertices[offset++] = q.y0; _vertices[offset++] = q.s0; _vertices[offset++] = q.t0;
                        _vertices[offset++] = q.x1; _vertices[offset++] = q.y0; _vertices[offset++] = q.s1; _vertices[offset++] = q.t0;
                        _vertices[offset++] = q.x1; _vertices[offset++] = q.y1; _vertices[offset++] = q.s1; _vertices[offset++] = q.t1;

                        _vertices[offset++] = q.x0; _vertices[offset++] = q.y0; _vertices[offset++] = q.s0; _vertices[offset++] = q.t0;
                        _vertices[offset++] = q.x1; _vertices[offset++] = q.y1; _vertices[offset++] = q.s1; _vertices[offset++] = q.t1;
                        _vertices[offset++] = q.x0; _vertices[offset++] = q.y1; _vertices[offset++] = q.s0; _vertices[offset++] = q.t1;
                        vertexCount = offset;
                    }
                }
                textY += _lineSpacing;
            }
        }

        if (vertexCount > 0)
        {
            GL.BufferData(BufferTarget.ArrayBuffer, vertexCount * sizeof(float), _vertices, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, vertexCount / 4);
        }

        // --- 4. Очистка состояния ---
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.Enable(EnableCap.DepthTest);
    }

    private unsafe float MeasureString(string text)
    {
        float width = 0;
        float x = 0, y = 0;
        fixed (StbTrueType.stbtt_bakedchar* pBakedChars = _bakedChars)
        {
            foreach (char c in text)
            {
                if (c >= FirstChar && c < FirstChar + NumChars)
                {
                    var q = new StbTrueType.stbtt_aligned_quad();
                    StbTrueType.stbtt_GetBakedQuad(pBakedChars, 1024, 1024, c - FirstChar, &x, &y, &q, 1);
                    width = q.x1;
                }
            }
        }
        return width;
    }

    #region IDisposable Implementation
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing) { _shader.Dispose(); }

            GL.DeleteBuffer(_vbo);
            GL.DeleteVertexArray(_vao);
            GL.DeleteTexture(_fontTexture);
            GL.DeleteTexture(_whiteTexture);

            _disposedValue = true;
        }
    }
    public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
    #endregion

    private class Shader : IDisposable
    {
        public readonly int Handle;
        private bool _disposed;
        public Shader(string vert, string frag)
        {
            var vs = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(vs, vert); Compile(vs);
            var fs = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(fs, frag); Compile(fs);
            Handle = GL.CreateProgram(); GL.AttachShader(Handle, vs); GL.AttachShader(Handle, fs); Link(Handle);
            GL.DetachShader(Handle, vs); GL.DetachShader(Handle, fs); GL.DeleteShader(vs); GL.DeleteShader(fs);
        }
        public void Use() => GL.UseProgram(Handle);
        public void SetMatrix4(string n, Matrix4 d) => GL.UniformMatrix4(GL.GetUniformLocation(Handle, n), false, ref d);
        public void SetVector4(string n, Vector4 d) => GL.Uniform4(GL.GetUniformLocation(Handle, n), d);
        public void SetInt(string n, int d) => GL.Uniform1(GL.GetUniformLocation(Handle, n), d);
        private void Compile(int s) { GL.CompileShader(s); GL.GetShader(s, ShaderParameter.CompileStatus, out var c); if (c == 0) throw new Exception(GL.GetShaderInfoLog(s)); }
        private void Link(int p) { GL.LinkProgram(p); GL.GetProgram(p, GetProgramParameterName.LinkStatus, out var c); if (c == 0) throw new Exception(GL.GetProgramInfoLog(p)); }
        public void Dispose() { if (!_disposed) { GL.DeleteProgram(Handle); _disposed = true; } }
    }
}