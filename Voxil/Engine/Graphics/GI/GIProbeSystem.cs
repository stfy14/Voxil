// --- Engine/Graphics/GI/GIProbeSystem.cs ---
// DDGI с октаэдрическими атласами irradiance + depth (Chebyshev visibility)
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;

public class GIProbeSystem : IDisposable
{
    // =========================================================
    // LOD 0 — ближняя сетка: шаг 1м, покрытие ~16м
    // =========================================================
    public const float PROBE_SPACING_L0 = 1.0f;
    public const int   PROBE_X = 32, PROBE_Y = 16, PROBE_Z = 32;
    public const int   PROBE_COUNT = PROBE_X * PROBE_Y * PROBE_Z; // 16 384

    // =========================================================
    // LOD 1 — средняя сетка: шаг 4м, покрытие ~64м
    // =========================================================
    public const float PROBE_SPACING_L1 = 4.0f;

    // =========================================================
    // LOD 2 — дальняя сетка: шаг 16м, покрытие ~256м
    // =========================================================
    public const float PROBE_SPACING_L2 = 16.0f;

    // Лучи на зонд по уровням
    public const int RAYS_PER_PROBE_L0 = 64;
    public const int RAYS_PER_PROBE_L1 = 32;
    public const int RAYS_PER_PROBE_L2 = 16;

    // Зондов за кадр
    public const int PROBES_PER_FRAME_L0 = 1024;
    public const int PROBES_PER_FRAME_L1 = 512;
    public const int PROBES_PER_FRAME_L2 = 256;

    // Для совместимости с UI
    public const float PROBE_SPACING    = PROBE_SPACING_L0;
    public const int   RAYS_PER_PROBE   = RAYS_PER_PROBE_L0;
    public const int   PROBES_PER_FRAME = PROBES_PER_FRAME_L0;

    // =========================================================
    // Размеры тайлов октаэдрического атласа
    // =========================================================
    public const int IRR_TILE   = 8;   // пикселей на зонд (irradiance)
    public const int DEPTH_TILE = 16;  // пикселей на зонд (depth)
    private const int IRR_PAD   = IRR_TILE   + 2; // 10 (1px border с каждой стороны)
    private const int DEPTH_PAD = DEPTH_TILE + 2; // 18

    // Раскладка тайлов в атласе:
    //   col = wx (0..PROBE_X-1)
    //   row = wy + wz * PROBE_Y (0..PROBE_Y*PROBE_Z-1)
    public static int IrrAtlasW   => PROBE_X * IRR_PAD;             // 320
    public static int IrrAtlasH   => PROBE_Y * PROBE_Z * IRR_PAD;   // 5120
    public static int DepthAtlasW => PROBE_X * DEPTH_PAD;           // 576
    public static int DepthAtlasH => PROBE_Y * PROBE_Z * DEPTH_PAD; // 9216

    // =========================================================
    // Position SSBOs (живость зондов + debug визуализация)
    // Структура: ProbeData = vec4 pos (w=alive) + vec4 colorAndState
    // =========================================================
    public int ProbePositionSsbo   { get; private set; }
    public int ProbePositionSsboL1 { get; private set; }
    public int ProbePositionSsboL2 { get; private set; }

    // =========================================================
    // Октаэдрические атласы (заменяют SH irradiance SSBO)
    // =========================================================
    public int IrrTexL0   { get; private set; }
    public int DepthTexL0 { get; private set; }
    public int IrrTexL1   { get; private set; }
    public int DepthTexL1 { get; private set; }
    public int IrrTexL2   { get; private set; }
    public int DepthTexL2 { get; private set; }

    // ── Состояние сеток ──────────────────────────────────────
    private int _gridBaseX,    _gridBaseY,    _gridBaseZ;
    private int _gridBaseX_L1, _gridBaseY_L1, _gridBaseZ_L1;
    private int _gridBaseX_L2, _gridBaseY_L2, _gridBaseZ_L2;

    private int _updateCursorL0 = 0;
    private int _updateCursorL1 = 0;
    private int _updateCursorL2 = 0;

    private readonly Shader _updateShader;
    private bool _disposed;

    public bool IsValid => _updateShader != null;

    // =========================================================
    public GIProbeSystem(Shader probeUpdateShader)
    {
        _updateShader = probeUpdateShader;
        InitBuffers();
        InitTextures();
        // Первичный бинд чтобы рендерер мог сразу использовать
        Bind();
    }

    // ─────────────────────────────────────────────────────────
    private void InitBuffers()
    {
        ProbePositionSsbo   = CreatePositionBuffer();
        ProbePositionSsboL1 = CreatePositionBuffer();
        ProbePositionSsboL2 = CreatePositionBuffer();
    }

    private static int CreatePositionBuffer()
    {
        float[] empty = new float[PROBE_COUNT * 8];
        Array.Fill(empty, -9999.0f);
        int ssbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ssbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer,
            empty.Length * sizeof(float), empty, BufferUsageHint.DynamicDraw);
        return ssbo;
    }

    // ─────────────────────────────────────────────────────────
    private void InitTextures()
    {
        // R11G11B10F — HDR irradiance, 4 байта/пиксель, без альфы
        IrrTexL0 = CreateAtlasTexture(IrrAtlasW, IrrAtlasH, SizedInternalFormat.R11fG11fB10f);
        IrrTexL1 = CreateAtlasTexture(IrrAtlasW, IrrAtlasH, SizedInternalFormat.R11fG11fB10f);
        IrrTexL2 = CreateAtlasTexture(IrrAtlasW, IrrAtlasH, SizedInternalFormat.R11fG11fB10f);

        // RG16F — mean depth + mean depth² для Chebyshev visibility test
        DepthTexL0 = CreateAtlasTexture(DepthAtlasW, DepthAtlasH, SizedInternalFormat.Rg16f);
        DepthTexL1 = CreateAtlasTexture(DepthAtlasW, DepthAtlasH, SizedInternalFormat.Rg16f);
        DepthTexL2 = CreateAtlasTexture(DepthAtlasW, DepthAtlasH, SizedInternalFormat.Rg16f);
    }

    private static int CreateAtlasTexture(int width, int height, SizedInternalFormat fmt)
    {
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, (PixelInternalFormat)fmt,
            width, height, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        // Linear — для плавной интерполяции между тайлами одного зонда
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        // ClampToEdge — border не "вытекает" в соседний зонд
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return tex;
    }

    // ─────────────────────────────────────────────────────────
    // Update — вызывается каждый кадр из GpuRaycastingRenderer
    // ─────────────────────────────────────────────────────────
    public void Update(Vector3 cameraPosition, Vector3 sunDir, float time,
                       int boundMinX, int boundMinY, int boundMinZ,
                       int boundMaxX, int boundMaxY, int boundMaxZ,
                       int maxRaySteps)
    {
        if (!IsValid) return;

        // ── L0 ──────────────────────────────────────────────
        ComputeGridBase(cameraPosition, PROBE_SPACING_L0,
            out _gridBaseX, out _gridBaseY, out _gridBaseZ);
        DispatchLevel(
            ProbePositionSsbo, IrrTexL0, DepthTexL0,
            _gridBaseX, _gridBaseY, _gridBaseZ,
            PROBE_SPACING_L0, RAYS_PER_PROBE_L0, PROBES_PER_FRAME_L0,
            ref _updateCursorL0,
            sunDir, time,
            boundMinX, boundMinY, boundMinZ,
            boundMaxX, boundMaxY, boundMaxZ,
            maxRaySteps / 2);

        // ── L1 ──────────────────────────────────────────────
        ComputeGridBase(cameraPosition, PROBE_SPACING_L1,
            out _gridBaseX_L1, out _gridBaseY_L1, out _gridBaseZ_L1);
        DispatchLevel(
            ProbePositionSsboL1, IrrTexL1, DepthTexL1,
            _gridBaseX_L1, _gridBaseY_L1, _gridBaseZ_L1,
            PROBE_SPACING_L1, RAYS_PER_PROBE_L1, PROBES_PER_FRAME_L1,
            ref _updateCursorL1,
            sunDir, time,
            boundMinX, boundMinY, boundMinZ,
            boundMaxX, boundMaxY, boundMaxZ,
            maxRaySteps / 4);

        // ── L2 ──────────────────────────────────────────────
        ComputeGridBase(cameraPosition, PROBE_SPACING_L2,
            out _gridBaseX_L2, out _gridBaseY_L2, out _gridBaseZ_L2);
        DispatchLevel(
            ProbePositionSsboL2, IrrTexL2, DepthTexL2,
            _gridBaseX_L2, _gridBaseY_L2, _gridBaseZ_L2,
            PROBE_SPACING_L2, RAYS_PER_PROBE_L2, PROBES_PER_FRAME_L2,
            ref _updateCursorL2,
            sunDir, time,
            boundMinX, boundMinY, boundMinZ,
            boundMaxX, boundMaxY, boundMaxZ,
            maxRaySteps / 8);
    }

    private static void ComputeGridBase(Vector3 camPos, float spacing,
                                        out int bx, out int by, out int bz)
    {
        bx = (int)Math.Floor(camPos.X / spacing - PROBE_X * 0.5f);
        by = (int)Math.Floor(camPos.Y / spacing - PROBE_Y * 0.5f);
        bz = (int)Math.Floor(camPos.Z / spacing - PROBE_Z * 0.5f);
    }

    private void DispatchLevel(
        int posSSBO, int irrTex, int depthTex,
        int baseX, int baseY, int baseZ,
        float spacing, int raysPerProbe, int probesThisFrame,
        ref int cursor,
        Vector3 sunDir, float time,
        int bMinX, int bMinY, int bMinZ,
        int bMaxX, int bMaxY, int bMaxZ,
        int maxRaySteps)
    {
        // Position SSBO на binding 16 — compute читает/пишет alive state
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, posSSBO);

        // Атласы на image bindings 2 и 3 — compute пишет, читает (temporal blend)
        GL.BindImageTexture(2, irrTex,   0, false, 0,
            TextureAccess.ReadWrite, SizedInternalFormat.R11fG11fB10f);
        GL.BindImageTexture(3, depthTex, 0, false, 0,
            TextureAccess.ReadWrite, SizedInternalFormat.Rg16f);

        _updateShader.Use();
        _updateShader.SetInt("uGIGridBaseX",     baseX);
        _updateShader.SetInt("uGIGridBaseY",     baseY);
        _updateShader.SetInt("uGIGridBaseZ",     baseZ);
        _updateShader.SetInt("uProbeStartIndex", cursor);
        _updateShader.SetInt("uProbesThisFrame", probesThisFrame);
        _updateShader.SetInt("uRaysPerProbe",    raysPerProbe);
        _updateShader.SetFloat("uTime",          time);
        _updateShader.SetVector3("uSunDir",      sunDir);
        _updateShader.SetFloat("uProbeSpacing",  spacing);
        _updateShader.SetInt("uProbeGridX",      PROBE_X);
        _updateShader.SetInt("uProbeGridY",      PROBE_Y);
        _updateShader.SetInt("uProbeGridZ",      PROBE_Z);
        _updateShader.SetInt("uBoundMinX",       bMinX);
        _updateShader.SetInt("uBoundMinY",       bMinY);
        _updateShader.SetInt("uBoundMinZ",       bMinZ);
        _updateShader.SetInt("uBoundMaxX",       bMaxX);
        _updateShader.SetInt("uBoundMaxY",       bMaxY);
        _updateShader.SetInt("uBoundMaxZ",       bMaxZ);
        _updateShader.SetInt("uMaxRaySteps",     maxRaySteps);

        GL.DispatchCompute((probesThisFrame + 63) / 64, 1, 1);
        // Барьер на image access + SSBO чтобы следующий dispatch видел результат
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit
                       | MemoryBarrierFlags.ShaderStorageBarrierBit
                       | MemoryBarrierFlags.TextureFetchBarrierBit);

        cursor = (cursor + probesThisFrame) % PROBE_COUNT;
    }

    // ─────────────────────────────────────────────────────────
    // Bind — привязывает position SSBOs (нужно перед compute dispatch)
    // ─────────────────────────────────────────────────────────
    public void Bind()
    {
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, ProbePositionSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 19, ProbePositionSsboL1);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 21, ProbePositionSsboL2);
    }

    // ─────────────────────────────────────────────────────────
    // SetSamplingUniforms — вызывается перед composite pass
    // ─────────────────────────────────────────────────────────
    public void SetSamplingUniforms(Shader shader)
    {
        if (shader == null) return;

        // Grid bases для всех уровней
        shader.SetInt("uGIGridBaseX",      _gridBaseX);
        shader.SetInt("uGIGridBaseY",      _gridBaseY);
        shader.SetInt("uGIGridBaseZ",      _gridBaseZ);
        shader.SetFloat("uGIProbeSpacing", PROBE_SPACING_L0);

        shader.SetInt("uGIGridBaseX_L1",      _gridBaseX_L1);
        shader.SetInt("uGIGridBaseY_L1",      _gridBaseY_L1);
        shader.SetInt("uGIGridBaseZ_L1",      _gridBaseZ_L1);
        shader.SetFloat("uGIProbeSpacingL1",  PROBE_SPACING_L1);

        shader.SetInt("uGIGridBaseX_L2",      _gridBaseX_L2);
        shader.SetInt("uGIGridBaseY_L2",      _gridBaseY_L2);
        shader.SetInt("uGIGridBaseZ_L2",      _gridBaseZ_L2);
        shader.SetFloat("uGIProbeSpacingL2",  PROBE_SPACING_L2);

        // Размер сетки (одинаков для всех уровней)
        shader.SetInt("uGIProbeX", PROBE_X);
        shader.SetInt("uGIProbeY", PROBE_Y);
        shader.SetInt("uGIProbeZ", PROBE_Z);

        // Размеры тайлов для вычисления UV в атласе
        shader.SetInt("uIrrTile",   IRR_TILE);
        shader.SetInt("uDepthTile", DEPTH_TILE);

        // Текстуры атласов — units 10..15
        GL.ActiveTexture(TextureUnit.Texture10);
        GL.BindTexture(TextureTarget.Texture2D, IrrTexL0);
        shader.SetInt("uGIIrrAtlas",    10);

        GL.ActiveTexture(TextureUnit.Texture11);
        GL.BindTexture(TextureTarget.Texture2D, DepthTexL0);
        shader.SetInt("uGIDepthAtlas",  11);

        GL.ActiveTexture(TextureUnit.Texture12);
        GL.BindTexture(TextureTarget.Texture2D, IrrTexL1);
        shader.SetInt("uGIIrrAtlasL1",  12);

        GL.ActiveTexture(TextureUnit.Texture13);
        GL.BindTexture(TextureTarget.Texture2D, DepthTexL1);
        shader.SetInt("uGIDepthAtlasL1",13);

        GL.ActiveTexture(TextureUnit.Texture14);
        GL.BindTexture(TextureTarget.Texture2D, IrrTexL2);
        shader.SetInt("uGIIrrAtlasL2",  14);

        GL.ActiveTexture(TextureUnit.Texture15);
        GL.BindTexture(TextureTarget.Texture2D, DepthTexL2);
        shader.SetInt("uGIDepthAtlasL2",15);
    }

    // ─────────────────────────────────────────────────────────
    // Debug визуализация зондов (читает из position SSBO)
    // ─────────────────────────────────────────────────────────
    public void DrawProbeDebug(LineRenderer lineRenderer, Vector3 cameraPos)
    {
        DrawLevelDebug(lineRenderer, cameraPos, ProbePositionSsbo,
            PROBE_SPACING_L0, 12.0f,  new Vector3(0.9f, 0.8f, 0.2f));
        DrawLevelDebug(lineRenderer, cameraPos, ProbePositionSsboL1,
            PROBE_SPACING_L1, 64.0f,  new Vector3(0.2f, 0.8f, 0.9f));
        DrawLevelDebug(lineRenderer, cameraPos, ProbePositionSsboL2,
            PROBE_SPACING_L2, 256.0f, new Vector3(0.8f, 0.2f, 0.9f));
    }

    private static void DrawLevelDebug(LineRenderer lr, Vector3 camPos, int ssbo,
                                       float spacing, float drawRadius, Vector3 aliveColor)
    {
        if (ssbo == 0) return;
        float[] data = new float[PROBE_COUNT * 8];
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ssbo);
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer,
            IntPtr.Zero, data.Length * sizeof(float), data);

        float r2  = drawRadius * drawRadius;
        float box = spacing * 0.08f;

        for (int i = 0; i < PROBE_COUNT; i++)
        {
            float px = data[i * 8];
            if (px <= -9000.0f) continue;
            float py    = data[i * 8 + 1];
            float pz    = data[i * 8 + 2];
            float state = data[i * 8 + 3];
            Vector3 pos = new(px, py, pz);
            if ((pos - camPos).LengthSquared > r2) continue;
            Vector3 col = state < 0.5f ? new Vector3(0.2f, 0.2f, 0.2f) : aliveColor;
            lr.DrawBox(pos - new Vector3(box), pos + new Vector3(box), col);
        }
    }

    // ─────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (ProbePositionSsbo   != 0) GL.DeleteBuffer(ProbePositionSsbo);
        if (ProbePositionSsboL1 != 0) GL.DeleteBuffer(ProbePositionSsboL1);
        if (ProbePositionSsboL2 != 0) GL.DeleteBuffer(ProbePositionSsboL2);

        if (IrrTexL0   != 0) GL.DeleteTexture(IrrTexL0);
        if (DepthTexL0 != 0) GL.DeleteTexture(DepthTexL0);
        if (IrrTexL1   != 0) GL.DeleteTexture(IrrTexL1);
        if (DepthTexL1 != 0) GL.DeleteTexture(DepthTexL1);
        if (IrrTexL2   != 0) GL.DeleteTexture(IrrTexL2);
        if (DepthTexL2 != 0) GL.DeleteTexture(DepthTexL2);
    }
}