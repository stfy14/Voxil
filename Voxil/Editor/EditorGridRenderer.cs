// --- START OF FILE EditorGridRenderer.cs ---

using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;

public class EditorGridRenderer : IDisposable
{
    private int _vao;
    private int _vbo;
    private Shader _shader;

    public EditorGridRenderer()
    {
        string vert = @"
            #version 330 core
            layout (location = 0) in vec3 aPos;
            
            uniform mat4 uViewProj;
            uniform vec3 uSize;      
            uniform vec3 uOffset;    
            
            out vec3 vLocalPos;

            void main() {
                vec3 centered = aPos - 0.5;
                vec3 worldPos = (centered * uSize) + uOffset;
                vLocalPos = aPos; 
                gl_Position = uViewProj * vec4(worldPos, 1.0);
            }";

        string frag = @"
            #version 330 core
            in vec3 vLocalPos;
            out vec4 FragColor;

            uniform vec3 uVoxelCount;
            uniform vec4 uColor;

            void main() {
                vec3 uv = clamp(vLocalPos, 0.001, 0.999);
                vec3 pos = uv * uVoxelCount;

                vec3 f = fwidth(pos);
                vec3 grid = abs(fract(pos - 0.5) - 0.5) / f;
                float line = min(min(grid.x, grid.y), grid.z);

                float alpha = 1.0 - smoothstep(0.0, 1.2, line);

                vec3 borderDist = min(pos, uVoxelCount - pos);
                vec3 borderGrid = borderDist / f;
                float border = min(min(borderGrid.x, borderGrid.y), borderGrid.z);
                float borderAlpha = 1.0 - smoothstep(0.0, 1.5, border);

                float finalAlpha = max(alpha, borderAlpha);

                if (finalAlpha < 0.05) discard;

                FragColor = vec4(uColor.rgb, finalAlpha * uColor.a);
            }";

        _shader = new Shader(vert, frag, isSourceCode: true);
        InitCube();
    }

    public void Render(CameraData cam, Vector3 gridCells, float voxelSize, Vector4 color, Vector3 centerPos)
    {
        if (color.W <= 0.01f) return;

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Включаем Z-буфер, но не пишем в него, чтобы сетка не перекрывала саму себя
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(false);

        // ОТКЛЮЧАЕМ CULL FACE! Мы хотим видеть всю сетку (передние и задние грани).
        // Глубина от рейкастера скроет те грани, которые за горой.
        GL.Disable(EnableCap.CullFace);

        _shader.Use();
        _shader.SetMatrix4("uViewProj", cam.View * cam.Projection);
        _shader.SetVector3("uSize", gridCells * voxelSize);
        _shader.SetVector3("uOffset", centerPos);
        _shader.SetVector3("uVoxelCount", gridCells);
        _shader.SetVector4("uColor", color);

        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 36);
        GL.BindVertexArray(0);

        GL.Enable(EnableCap.CullFace);
        GL.CullFace(CullFaceMode.Back);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    private void InitCube()
    {
        float[] vertices = {
            0,0,0, 1,1,0, 1,0,0, 1,1,0, 0,0,0, 0,1,0,
            0,0,1, 1,0,1, 1,1,1, 1,1,1, 0,1,1, 0,0,1,
            0,1,1, 0,1,0, 0,0,0, 0,0,0, 0,0,1, 0,1,1,
            1,1,1, 1,0,0, 1,1,0, 1,0,0, 1,1,1, 1,0,1,
            0,0,0, 1,0,0, 1,0,1, 1,0,1, 0,0,1, 0,0,0,
            0,1,0, 1,1,1, 1,1,0, 1,1,1, 0,1,0, 0,1,1
        };

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        _shader?.Dispose();
    }
}