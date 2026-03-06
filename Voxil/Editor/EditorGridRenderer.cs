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
        // Вершинный шейдер: 
        // 1. Берет куб 0..1
        // 2. Центрирует его (-0.5 .. 0.5)
        // 3. Растягивает до размера uSize
        // 4. Сдвигает в позицию uOffset (центр объекта)
        string vert = @"
            #version 330 core
            layout (location = 0) in vec3 aPos;
            
            uniform mat4 uViewProj;
            uniform vec3 uSize;      
            uniform vec3 uOffset;    
            
            out vec3 vLocalPos; // 0..1 для расчета линий

            void main() {
                vec3 centered = aPos - 0.5;
                vec3 worldPos = (centered * uSize) + uOffset;
                vLocalPos = aPos; 
                gl_Position = uViewProj * vec4(worldPos, 1.0);
            }";

        // Фрагментный шейдер:
        // Рисует процедурную сетку.
        string frag = @"
            #version 330 core
            in vec3 vLocalPos;
            out vec4 FragColor;

            uniform float uVoxelCount;
            uniform vec4 uColor;

            void main() {
                // Отступ от краев 0.0 и 1.0, чтобы не было Z-fighting и шума на гранях
                vec3 uv = clamp(vLocalPos, 0.001, 0.999);

                // Координаты в пространстве вокселей
                vec3 pos = uv * uVoxelCount;

                // Магия fwidth для сглаживания линий любой толщины
                vec3 f = fwidth(pos);
                vec3 grid = abs(fract(pos - 0.5) - 0.5) / f;
                float line = min(min(grid.x, grid.y), grid.z);

                // Рисуем линии
                float alpha = 1.0 - smoothstep(0.0, 1.5, line);

                // Жирная рамка (Border) по краям
                vec3 borderDist = min(pos, uVoxelCount - pos);
                vec3 borderGrid = borderDist / f;
                float border = min(min(borderGrid.x, borderGrid.y), borderGrid.z);
                float borderAlpha = 1.0 - smoothstep(0.0, 2.0, border);

                float finalAlpha = max(alpha * 0.3, borderAlpha);

                if (finalAlpha < 0.05) discard;

                FragColor = vec4(uColor.rgb, finalAlpha * uColor.a);
            }";

        _shader = new Shader(vert, frag, isSourceCode: true);
        InitCube();
    }

    public void Render(CameraData cam, int gridSize, float voxelSize, Vector4 color, Vector3 centerPos)
    {
        if (color.W <= 0.01f) return;

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Enable(EnableCap.DepthTest);
        
        // ВАЖНО: Не пишем в Depth, чтобы полупрозрачная сетка не скрывала воксели внутри себя
        GL.DepthMask(false); 

        // ВАЖНО: Рисуем ВНУТРЕННИЕ грани куба (Front Cull).
        // Так мы видим сетку как "комнату" изнутри, и грани не накладываются друг на друга -> нет шума.
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(CullFaceMode.Front); 

        _shader.Use();
        _shader.SetMatrix4("uViewProj", cam.View * cam.Projection);
        _shader.SetVector3("uSize", new Vector3(gridSize * voxelSize));
        _shader.SetVector3("uOffset", centerPos);
        _shader.SetFloat("uVoxelCount", (float)gridSize);
        
        // Передача Vector4 (убедись, что добавил метод в Shader.cs!)
        _shader.SetVector4("uColor", color);

        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 36);
        GL.BindVertexArray(0);

        GL.CullFace(CullFaceMode.Back);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    private void InitCube()
    {
        // Обычный куб 0..1
        float[] vertices = {
            0,0,0, 1,1,0, 1,0,0, 1,1,0, 0,0,0, 0,1,0, // Back
            0,0,1, 1,0,1, 1,1,1, 1,1,1, 0,1,1, 0,0,1, // Front
            0,1,1, 0,1,0, 0,0,0, 0,0,0, 0,0,1, 0,1,1, // Left
            1,1,1, 1,0,0, 1,1,0, 1,0,0, 1,1,1, 1,0,1, // Right
            0,0,0, 1,0,0, 1,0,1, 1,0,1, 0,0,1, 0,0,0, // Bottom
            0,1,0, 1,1,1, 1,1,0, 1,1,1, 0,1,0, 0,1,1  // Top
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