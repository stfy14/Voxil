// --- START OF FILE GIProbeSystem.cs ---
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

public class GIProbeSystem : IDisposable
{
    private int _debugVbo;

    public const float PROBE_SPACING_L0 = 1.0f;
    public const int PROBE_X = 32, PROBE_Y = 16, PROBE_Z = 32;
    public const int PROBE_COUNT = PROBE_X * PROBE_Y * PROBE_Z;

    public const float PROBE_SPACING_L1 = 4.0f;
    public const float PROBE_SPACING_L2 = 16.0f;

    public const int RAYS_PER_PROBE_L0 = 64;
    public const int RAYS_PER_PROBE_L1 = 32;
    public const int RAYS_PER_PROBE_L2 = 16;

    // Чтобы не убить GPU количеством лучей, можно снизить число обновляемых зондов:
    public const int PROBES_PER_FRAME_L0 = 512;
    public const int PROBES_PER_FRAME_L1 = 256;  // Было 512
    public const int PROBES_PER_FRAME_L2 = 128;  // Было 256

    public const float PROBE_SPACING = PROBE_SPACING_L0;
    public const int RAYS_PER_PROBE = RAYS_PER_PROBE_L0;
    public const int PROBES_PER_FRAME = PROBES_PER_FRAME_L0;

    public const int IRR_TILE = 8;
    public const int DEPTH_TILE = 16;
    private const int IRR_PAD = IRR_TILE + 2;
    private const int DEPTH_PAD = DEPTH_TILE + 2;

    public static int IrrAtlasW => PROBE_X * IRR_PAD;
    public static int IrrAtlasH => PROBE_Y * PROBE_Z * IRR_PAD;
    public static int DepthAtlasW => PROBE_X * DEPTH_PAD;
    public static int DepthAtlasH => PROBE_Y * PROBE_Z * DEPTH_PAD;

    public int ProbePositionSsbo { get; private set; }
    public int ProbePositionSsboL1 { get; private set; }
    public int ProbePositionSsboL2 { get; private set; }

    public int IrrTexL0 { get; private set; }
    public int DepthTexL0 { get; private set; }
    public int IrrTexL1 { get; private set; }
    public int DepthTexL1 { get; private set; }
    public int IrrTexL2 { get; private set; }
    public int DepthTexL2 { get; private set; }

    private int _updateListSsbo;
    private int[] _updateBufferCPU = new int[PROBES_PER_FRAME_L0];

    private class LevelData
    {
        public int GridBaseX = int.MinValue, GridBaseY = int.MinValue, GridBaseZ = int.MinValue;
        public Vector3i[] CachedGridCoords = new Vector3i[PROBE_COUNT];
        public bool[] IsInPriority = new bool[PROBE_COUNT];
        public Queue<int> PriorityQueue = new Queue<int>();
        public int RoundRobinCursor = 0;

        public LevelData() { Array.Fill(CachedGridCoords, new Vector3i(int.MinValue)); }
    }

    private LevelData _l0 = new LevelData();
    private LevelData _l1 = new LevelData();
    private LevelData _l2 = new LevelData();

    private readonly Shader _updateShader;
    private bool _disposed;

    // --- Для дебага (цельные кубики) ---
    private Shader _debugShader;
    private int _debugVao;

    public bool IsValid => _updateShader != null;

    public GIProbeSystem(Shader probeUpdateShader)
    {
        _updateShader = probeUpdateShader;
        InitBuffers();
        InitTextures();
        InitDebugRenderer();
        Bind();
    }

    private void InitBuffers()
    {
        ProbePositionSsbo = CreatePositionBuffer();
        ProbePositionSsboL1 = CreatePositionBuffer();
        ProbePositionSsboL2 = CreatePositionBuffer();

        _updateListSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _updateListSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, PROBES_PER_FRAME_L0 * sizeof(int), IntPtr.Zero, BufferUsageHint.DynamicDraw);
    }

    private static int CreatePositionBuffer()
    {
        float[] empty = new float[PROBE_COUNT * 8];
        Array.Fill(empty, -9999.0f);
        int ssbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ssbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, empty.Length * sizeof(float), empty, BufferUsageHint.DynamicDraw);
        return ssbo;
    }

    private void InitTextures()
    {
        IrrTexL0 = CreateAtlasTexture(IrrAtlasW, IrrAtlasH, SizedInternalFormat.R11fG11fB10f);
        IrrTexL1 = CreateAtlasTexture(IrrAtlasW, IrrAtlasH, SizedInternalFormat.R11fG11fB10f);
        IrrTexL2 = CreateAtlasTexture(IrrAtlasW, IrrAtlasH, SizedInternalFormat.R11fG11fB10f);

        DepthTexL0 = CreateAtlasTexture(DepthAtlasW, DepthAtlasH, SizedInternalFormat.Rg32f);
        DepthTexL1 = CreateAtlasTexture(DepthAtlasW, DepthAtlasH, SizedInternalFormat.Rg32f);
        DepthTexL2 = CreateAtlasTexture(DepthAtlasW, DepthAtlasH, SizedInternalFormat.Rg32f);
    }

    private static int CreateAtlasTexture(int width, int height, SizedInternalFormat fmt)
    {
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, (PixelInternalFormat)fmt, width, height, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return tex;
    }

    private void InitDebugRenderer()
    {
        string vert = @"
        #version 450 core
        layout(location = 0) in vec3 aPos;

        struct ProbeData { vec4 pos; vec4 color; };
        layout(std430, binding = 16) buffer ProbeBuffer { ProbeData probes[]; };
        
        uniform mat4 uViewProj;
        uniform float uCubeSize; // <--- НОВОЕ: Динамический размер кубика
        out vec3 vColor;
        
        void main() {
            ProbeData p = probes[gl_InstanceID];
            if (p.pos.w < 0.5) { 
                gl_Position = vec4(10000.0, 10000.0, 10000.0, 1.0); 
                vColor = vec3(0.0);
            } else {
                vec3 localPos = aPos * uCubeSize; // <--- ИСПОЛЬЗУЕМ UNIFORM
                vec3 worldPos = p.pos.xyz + localPos;
                
                gl_Position = uViewProj * vec4(worldPos, 1.0);
                vColor = p.color.rgb; 
            }
        }";

        string frag = @"
        #version 450 core
        in vec3 vColor;
        out vec4 FragColor;
        void main() {
            vec3 c = vColor / (1.0 + vColor);
            FragColor = vec4(pow(max(c, vec3(0.0)), vec3(1.0/2.2)), 1.0); 
        }";

        _debugShader = new Shader(vert, frag, true);

        // Физические вершины куба
        float[] cubeVerts = new float[] {
            -1,-1,-1,  1,-1,-1,  1, 1,-1,  1, 1,-1, -1, 1,-1, -1,-1,-1,
        -1,-1, 1,  1,-1, 1,  1, 1, 1,  1, 1, 1, -1, 1, 1, -1,-1, 1,
        -1, 1, 1, -1, 1,-1, -1,-1,-1, -1,-1,-1, -1,-1, 1, -1, 1, 1,
         1, 1, 1,  1, 1,-1,  1,-1,-1,  1,-1,-1,  1,-1, 1,  1, 1, 1,
        -1,-1,-1,  1,-1,-1,  1,-1, 1,  1,-1, 1, -1,-1, 1, -1,-1,-1,
        -1, 1,-1,  1, 1,-1,  1, 1, 1,  1, 1, 1, -1, 1, 1, -1, 1,-1
        }
        ;

        _debugVao = GL.GenVertexArray();
        _debugVbo = GL.GenBuffer();

        GL.BindVertexArray(_debugVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _debugVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, cubeVerts.Length * sizeof(float), cubeVerts, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);

        GL.BindVertexArray(0);
    }

    private static int TrueMod(int a, int b) { int m = a % b; return m < 0 ? m + b : m; }

    public void MarkProbesDirty(Vector3 worldPos)
    {
        MarkLevelDirty(_l0, worldPos, PROBE_SPACING_L0);
        MarkLevelDirty(_l1, worldPos, PROBE_SPACING_L1);
        MarkLevelDirty(_l2, worldPos, PROBE_SPACING_L2);
    }

    private void MarkLevelDirty(LevelData level, Vector3 pos, float spacing)
    {
        int gx = (int)Math.Floor(pos.X / spacing);
        int gy = (int)Math.Floor(pos.Y / spacing);
        int gz = (int)Math.Floor(pos.Z / spacing);

        for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cx = gx + dx; int cy = gy + dy; int cz = gz + dz;
                    int wx = TrueMod(cx, PROBE_X); int wy = TrueMod(cy, PROBE_Y); int wz = TrueMod(cz, PROBE_Z);
                    int idx = wx + wy * PROBE_X + wz * PROBE_X * PROBE_Y;
                    if (level.CachedGridCoords[idx] == new Vector3i(cx, cy, cz))
                    {
                        if (!level.IsInPriority[idx]) { level.IsInPriority[idx] = true; level.PriorityQueue.Enqueue(idx); }
                    }
                }
    }

    public void Update(Vector3 cameraPosition, Vector3 sunDir, float time,
                       int boundMinX, int boundMinY, int boundMinZ,
                       int boundMaxX, int boundMaxY, int boundMaxZ,
                       int maxRaySteps,
                       Vector3 gridOrigin, float gridStep, int gridSize, int objectCount, int pointLightCount)
    {
        if (!IsValid) return;

        UpdateLevel(_l0, ProbePositionSsbo, IrrTexL0, DepthTexL0, cameraPosition, PROBE_SPACING_L0, RAYS_PER_PROBE_L0, PROBES_PER_FRAME_L0, sunDir, time, boundMinX, boundMinY, boundMinZ, boundMaxX, boundMaxY, boundMaxZ, maxRaySteps / 2, gridOrigin, gridStep, gridSize, objectCount, pointLightCount);
        UpdateLevel(_l1, ProbePositionSsboL1, IrrTexL1, DepthTexL1, cameraPosition, PROBE_SPACING_L1, RAYS_PER_PROBE_L1, PROBES_PER_FRAME_L1, sunDir, time, boundMinX, boundMinY, boundMinZ, boundMaxX, boundMaxY, boundMaxZ, maxRaySteps / 4, gridOrigin, gridStep, gridSize, objectCount, pointLightCount);
        UpdateLevel(_l2, ProbePositionSsboL2, IrrTexL2, DepthTexL2, cameraPosition, PROBE_SPACING_L2, RAYS_PER_PROBE_L2, PROBES_PER_FRAME_L2, sunDir, time, boundMinX, boundMinY, boundMinZ, boundMaxX, boundMaxY, boundMaxZ, maxRaySteps / 8, gridOrigin, gridStep, gridSize, objectCount, pointLightCount);
    }

    private void UpdateLevel(LevelData level, int posSSBO, int irrTex, int depthTex, Vector3 camPos, float spacing, int raysPerProbe, int probesThisFrame, Vector3 sunDir, float time, int bMinX, int bMinY, int bMinZ, int bMaxX, int bMaxY, int bMaxZ, int maxRaySteps, Vector3 gridOrigin, float gridStep, int gridSize, int objectCount, int pointLightCount)
    {
        int bx = (int)Math.Floor(camPos.X / spacing - PROBE_X * 0.5f);
        int by = (int)Math.Floor(camPos.Y / spacing - PROBE_Y * 0.5f);
        int bz = (int)Math.Floor(camPos.Z / spacing - PROBE_Z * 0.5f);

        if (bx != level.GridBaseX || by != level.GridBaseY || bz != level.GridBaseZ)
        {
            level.GridBaseX = bx; level.GridBaseY = by; level.GridBaseZ = bz;
            for (int i = 0; i < PROBE_COUNT; i++)
            {
                int wx = i % PROBE_X; int wy = (i / PROBE_X) % PROBE_Y; int wz = i / (PROBE_X * PROBE_Y);
                int modBaseX = TrueMod(bx, PROBE_X); int modBaseY = TrueMod(by, PROBE_Y); int modBaseZ = TrueMod(bz, PROBE_Z);
                int gx = bx + TrueMod(wx - modBaseX, PROBE_X); int gy = by + TrueMod(wy - modBaseY, PROBE_Y); int gz = bz + TrueMod(wz - modBaseZ, PROBE_Z);
                var expectedCoord = new Vector3i(gx, gy, gz);

                if (level.CachedGridCoords[i] != expectedCoord)
                {
                    level.CachedGridCoords[i] = expectedCoord;
                    if (!level.IsInPriority[i]) { level.IsInPriority[i] = true; level.PriorityQueue.Enqueue(i); }
                }
            }
        }

        int updateCount = 0;
        while (updateCount < probesThisFrame && level.PriorityQueue.Count > 0)
        {
            int idx = level.PriorityQueue.Dequeue(); level.IsInPriority[idx] = false;
            _updateBufferCPU[updateCount++] = idx;
        }

        while (updateCount < probesThisFrame)
        {
            int idx = level.RoundRobinCursor;
            level.RoundRobinCursor = (level.RoundRobinCursor + 1) % PROBE_COUNT;
            if (!level.IsInPriority[idx]) _updateBufferCPU[updateCount++] = idx;
        }

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _updateListSsbo);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, updateCount * sizeof(int), _updateBufferCPU);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 22, _updateListSsbo);

        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, posSSBO);
        GL.BindImageTexture(2, irrTex, 0, false, 0, TextureAccess.ReadWrite, SizedInternalFormat.R11fG11fB10f);
        GL.BindImageTexture(3, depthTex, 0, false, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rg32f);

        // ИСПРАВЛЕНИЕ: Было захардкожено IrrTexL0. Теперь каждый каскад читает СВОЮ историю света!
        GL.ActiveTexture(TextureUnit.Texture5);
        GL.BindTexture(TextureTarget.Texture2D, irrTex);

        _updateShader.Use();
        _updateShader.SetInt("uBounceIrrAtlas", 5);
        _updateShader.SetInt("uGIGridBaseX", bx);
        _updateShader.SetInt("uGIGridBaseY", by);
        _updateShader.SetInt("uGIGridBaseZ", bz);
        _updateShader.SetInt("uProbesThisFrame", updateCount);
        _updateShader.SetInt("uRaysPerProbe", raysPerProbe);
        _updateShader.SetFloat("uTime", time);
        _updateShader.SetVector3("uSunDir", sunDir);
        _updateShader.SetFloat("uProbeSpacing", spacing);
        _updateShader.SetInt("uProbeGridX", PROBE_X);
        _updateShader.SetInt("uProbeGridY", PROBE_Y);
        _updateShader.SetInt("uProbeGridZ", PROBE_Z);
        _updateShader.SetInt("uBoundMinX", bMinX);
        _updateShader.SetInt("uBoundMinY", bMinY);
        _updateShader.SetInt("uBoundMinZ", bMinZ);
        _updateShader.SetInt("uBoundMaxX", bMaxX);
        _updateShader.SetInt("uBoundMaxY", bMaxY);
        _updateShader.SetInt("uBoundMaxZ", bMaxZ);
        _updateShader.SetInt("uMaxRaySteps", maxRaySteps);

        _updateShader.SetVector3("uGridOrigin", gridOrigin);
        _updateShader.SetFloat("uGridStep", gridStep);
        _updateShader.SetInt("uGridSize", gridSize);
        _updateShader.SetInt("uObjectCount", objectCount);
        _updateShader.SetInt("uPointLightCount", pointLightCount);

        GL.DispatchCompute((updateCount + 63) / 64, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
    }

    public void Bind()
    {
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, ProbePositionSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 19, ProbePositionSsboL1);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 21, ProbePositionSsboL2);
    }

    public void SetSamplingUniforms(Shader shader)
    {
        if (shader == null) return;
        shader.SetInt("uGIGridBaseX", _l0.GridBaseX); shader.SetInt("uGIGridBaseY", _l0.GridBaseY); shader.SetInt("uGIGridBaseZ", _l0.GridBaseZ);
        shader.SetFloat("uGIProbeSpacing", PROBE_SPACING_L0);
        shader.SetInt("uGIGridBaseX_L1", _l1.GridBaseX); shader.SetInt("uGIGridBaseY_L1", _l1.GridBaseY); shader.SetInt("uGIGridBaseZ_L1", _l1.GridBaseZ);
        shader.SetFloat("uGIProbeSpacingL1", PROBE_SPACING_L1);
        shader.SetInt("uGIGridBaseX_L2", _l2.GridBaseX); shader.SetInt("uGIGridBaseY_L2", _l2.GridBaseY); shader.SetInt("uGIGridBaseZ_L2", _l2.GridBaseZ);
        shader.SetFloat("uGIProbeSpacingL2", PROBE_SPACING_L2);

        shader.SetInt("uGIProbeX", PROBE_X); shader.SetInt("uGIProbeY", PROBE_Y); shader.SetInt("uGIProbeZ", PROBE_Z);
        shader.SetInt("uIrrTile", IRR_TILE); shader.SetInt("uDepthTile", DEPTH_TILE);

        GL.ActiveTexture(TextureUnit.Texture10); GL.BindTexture(TextureTarget.Texture2D, IrrTexL0); shader.SetInt("uGIIrrAtlas", 10);
        GL.ActiveTexture(TextureUnit.Texture11); GL.BindTexture(TextureTarget.Texture2D, DepthTexL0); shader.SetInt("uGIDepthAtlas", 11);
        GL.ActiveTexture(TextureUnit.Texture12); GL.BindTexture(TextureTarget.Texture2D, IrrTexL1); shader.SetInt("uGIIrrAtlasL1", 12);
        GL.ActiveTexture(TextureUnit.Texture13); GL.BindTexture(TextureTarget.Texture2D, DepthTexL1); shader.SetInt("uGIDepthAtlasL1", 13);
        GL.ActiveTexture(TextureUnit.Texture14); GL.BindTexture(TextureTarget.Texture2D, IrrTexL2); shader.SetInt("uGIIrrAtlasL2", 14);
        GL.ActiveTexture(TextureUnit.Texture15); GL.BindTexture(TextureTarget.Texture2D, DepthTexL2); shader.SetInt("uGIDepthAtlasL2", 15);
    }

    public void DrawSolidProbes(CameraData cam)
    {
        if (_debugShader == null || !GameSettings.ShowGIProbes) return;

        // ИСПРАВЛЕНИЕ: Читаем настройку X-Ray!
        if (GameSettings.ShowGIProbesXRay)
            GL.Disable(EnableCap.DepthTest); // Рентген: видно сквозь стены
        else
            GL.Enable(EnableCap.DepthTest);  // Обычный: кубики прячутся за горами

        _debugShader.Use();
        _debugShader.SetMatrix4("uViewProj", cam.View * cam.Projection);

        GL.BindVertexArray(_debugVao);

        int lod = GameSettings.GIDebugLOD;

        // Вспомогательная функция для отрисовки нужного уровня
        void DrawLevel(int ssbo, float size)
        {
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, ssbo);
            _debugShader.SetFloat("uCubeSize", size);
            GL.DrawArraysInstanced(PrimitiveType.Triangles, 0, 36, PROBE_COUNT);
        }

        if (lod == 0 || lod == -1) DrawLevel(ProbePositionSsbo, 0.075f);
        if (lod == 1 || lod == -1) DrawLevel(ProbePositionSsboL1, 0.3f);
        if (lod == 2 || lod == -1) DrawLevel(ProbePositionSsboL2, 1.2f);

        GL.BindVertexArray(0);

        // Обязательно возвращаем стандартный DepthTest для остального движка
        GL.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (ProbePositionSsbo != 0) GL.DeleteBuffer(ProbePositionSsbo);
        if (ProbePositionSsboL1 != 0) GL.DeleteBuffer(ProbePositionSsboL1);
        if (ProbePositionSsboL2 != 0) GL.DeleteBuffer(ProbePositionSsboL2);
        if (_updateListSsbo != 0) GL.DeleteBuffer(_updateListSsbo);

        if (IrrTexL0 != 0) GL.DeleteTexture(IrrTexL0); if (DepthTexL0 != 0) GL.DeleteTexture(DepthTexL0);
        if (IrrTexL1 != 0) GL.DeleteTexture(IrrTexL1); if (DepthTexL1 != 0) GL.DeleteTexture(DepthTexL1);
        if (IrrTexL2 != 0) GL.DeleteTexture(IrrTexL2); if (DepthTexL2 != 0) GL.DeleteTexture(DepthTexL2);

        _debugShader?.Dispose();
        if (_debugVao != 0) GL.DeleteVertexArray(_debugVao);
        if (_debugVbo != 0) GL.DeleteBuffer(_debugVbo);
    }
}