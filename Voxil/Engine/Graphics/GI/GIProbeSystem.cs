// --- Engine/Graphics/GI/GIProbeSystem.cs ---
// 3-уровневая LOD система зондов для бесконечной дальности GI
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;

public class GIProbeSystem : IDisposable
{
    // =========================================================
    // LOD 0 — ближняя сетка: шаг 1м, покрытие ~16м
    // Высокое качество, обновляется быстро
    // =========================================================
    public const float PROBE_SPACING_L0 = 1.0f;
    public const int   PROBE_X = 32, PROBE_Y = 16, PROBE_Z = 32;
    public const int   PROBE_COUNT = PROBE_X * PROBE_Y * PROBE_Z; // 16 384

    // =========================================================
    // LOD 1 — средняя сетка: шаг 4м, покрытие ~64м
    // Среднее качество
    // =========================================================
    public const float PROBE_SPACING_L1 = 4.0f;
    // Та же сетка 32×16×32, но с другим шагом

    // =========================================================
    // LOD 2 — дальняя сетка: шаг 16м, покрытие ~256м
    // Грубое освещение, но покрывает любую дальность прорисовки
    // =========================================================
    public const float PROBE_SPACING_L2 = 16.0f;

    // Лучи на зонд по уровням (L0 = полное качество, L2 = минимальное)
    public const int RAYS_PER_PROBE_L0 = 64;
    public const int RAYS_PER_PROBE_L1 = 32;
    public const int RAYS_PER_PROBE_L2 = 16;

    // Зондов за кадр: L0 обновляется быстрее, L2 — реже (они далеко)
    public const int PROBES_PER_FRAME_L0 = 1024; // полный цикл за 16 кадров
    public const int PROBES_PER_FRAME_L1 = 512;  // полный цикл за 32 кадра
    public const int PROBES_PER_FRAME_L2 = 256;  // полный цикл за 64 кадра

    // Для совместимости (используется в UI)
    public const float PROBE_SPACING   = PROBE_SPACING_L0;
    public const int   RAYS_PER_PROBE  = RAYS_PER_PROBE_L0;
    public const int   PROBES_PER_FRAME = PROBES_PER_FRAME_L0;

    // ── SSBOs ──────────────────────────────────────────────
    // L0: binding 16, 17
    public int ProbePositionSsbo    { get; private set; }
    public int ProbeIrradianceSsbo  { get; private set; }
    // L1: binding 19, 20
    public int ProbePositionSsboL1   { get; private set; }
    public int ProbeIrradianceSsboL1 { get; private set; }
    // L2: binding 21, 22
    public int ProbePositionSsboL2   { get; private set; }
    public int ProbeIrradianceSsboL2 { get; private set; }

    // ── Состояние сеток ───────────────────────────────────
    private int _gridBaseX,    _gridBaseY,    _gridBaseZ;
    private int _gridBaseX_L1, _gridBaseY_L1, _gridBaseZ_L1;
    private int _gridBaseX_L2, _gridBaseY_L2, _gridBaseZ_L2;

    private int _updateCursorL0 = 0;
    private int _updateCursorL1 = 0;
    private int _updateCursorL2 = 0;

    private readonly Shader _updateShader;
    private bool _disposed;

    public bool IsValid => _updateShader != null;

    public GIProbeSystem(Shader probeUpdateShader)
    {
        _updateShader = probeUpdateShader;
        InitBuffers();
    }

    private void InitBuffers()
    {
        ProbePositionSsbo    = CreatePositionBuffer();
        ProbeIrradianceSsbo  = CreateIrradianceBuffer();
        ProbePositionSsboL1  = CreatePositionBuffer();
        ProbeIrradianceSsboL1 = CreateIrradianceBuffer();
        ProbePositionSsboL2  = CreatePositionBuffer();
        ProbeIrradianceSsboL2 = CreateIrradianceBuffer();

        Bind();
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

    private static int CreateIrradianceBuffer()
    {
        float[] empty = new float[PROBE_COUNT * 12];
        int ssbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ssbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, empty.Length * sizeof(float), empty, BufferUsageHint.DynamicDraw);
        return ssbo;
    }

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
            posSSBO: ProbePositionSsbo, irrSSBO: ProbeIrradianceSsbo,
            baseX: _gridBaseX, baseY: _gridBaseY, baseZ: _gridBaseZ,
            spacing: PROBE_SPACING_L0,
            raysPerProbe: RAYS_PER_PROBE_L0,
            probesThisFrame: PROBES_PER_FRAME_L0,
            ref _updateCursorL0,
            sunDir, time,
            boundMinX, boundMinY, boundMinZ,
            boundMaxX, boundMaxY, boundMaxZ,
            maxRaySteps / 2
        );

        // ── L1 ──────────────────────────────────────────────
        ComputeGridBase(cameraPosition, PROBE_SPACING_L1,
                        out _gridBaseX_L1, out _gridBaseY_L1, out _gridBaseZ_L1);

        DispatchLevel(
            posSSBO: ProbePositionSsboL1, irrSSBO: ProbeIrradianceSsboL1,
            baseX: _gridBaseX_L1, baseY: _gridBaseY_L1, baseZ: _gridBaseZ_L1,
            spacing: PROBE_SPACING_L1,
            raysPerProbe: RAYS_PER_PROBE_L1,
            probesThisFrame: PROBES_PER_FRAME_L1,
            ref _updateCursorL1,
            sunDir, time,
            boundMinX, boundMinY, boundMinZ,
            boundMaxX, boundMaxY, boundMaxZ,
            maxRaySteps / 4
        );

        // ── L2 ──────────────────────────────────────────────
        ComputeGridBase(cameraPosition, PROBE_SPACING_L2,
                        out _gridBaseX_L2, out _gridBaseY_L2, out _gridBaseZ_L2);

        DispatchLevel(
            posSSBO: ProbePositionSsboL2, irrSSBO: ProbeIrradianceSsboL2,
            baseX: _gridBaseX_L2, baseY: _gridBaseY_L2, baseZ: _gridBaseZ_L2,
            spacing: PROBE_SPACING_L2,
            raysPerProbe: RAYS_PER_PROBE_L2,
            probesThisFrame: PROBES_PER_FRAME_L2,
            ref _updateCursorL2,
            sunDir, time,
            boundMinX, boundMinY, boundMinZ,
            boundMaxX, boundMaxY, boundMaxZ,
            maxRaySteps / 8
        );
    }

    private static void ComputeGridBase(Vector3 camPos, float spacing,
                                        out int bx, out int by, out int bz)
    {
        bx = (int)Math.Floor(camPos.X / spacing - PROBE_X * 0.5f);
        by = (int)Math.Floor(camPos.Y / spacing - PROBE_Y * 0.5f);
        bz = (int)Math.Floor(camPos.Z / spacing - PROBE_Z * 0.5f);
    }

    private void DispatchLevel(
        int posSSBO, int irrSSBO,
        int baseX, int baseY, int baseZ,
        float spacing, int raysPerProbe, int probesThisFrame,
        ref int cursor,
        Vector3 sunDir, float time,
        int bMinX, int bMinY, int bMinZ,
        int bMaxX, int bMaxY, int bMaxZ,
        int maxRaySteps)
    {
        // Привязываем буферы этого уровня
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, posSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 17, irrSSBO);

        _updateShader.Use();
        _updateShader.SetInt("uGIGridBaseX",    baseX);
        _updateShader.SetInt("uGIGridBaseY",    baseY);
        _updateShader.SetInt("uGIGridBaseZ",    baseZ);
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
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        cursor = (cursor + probesThisFrame) % PROBE_COUNT;
    }

    // ─────────────────────────────────────────────────────────
    public void Bind()
    {
        // L0
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, ProbePositionSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 17, ProbeIrradianceSsbo);
        // L1
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 19, ProbePositionSsboL1);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 20, ProbeIrradianceSsboL1);
        // L2
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 21, ProbePositionSsboL2);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 22, ProbeIrradianceSsboL2);
    }

    public void SetSamplingUniforms(Shader shader)
    {
        if (shader == null) return;

        // L0
        shader.SetInt("uGIGridBaseX",    _gridBaseX);
        shader.SetInt("uGIGridBaseY",    _gridBaseY);
        shader.SetInt("uGIGridBaseZ",    _gridBaseZ);
        shader.SetFloat("uGIProbeSpacing", PROBE_SPACING_L0);

        // L1
        shader.SetInt("uGIGridBaseX_L1",    _gridBaseX_L1);
        shader.SetInt("uGIGridBaseY_L1",    _gridBaseY_L1);
        shader.SetInt("uGIGridBaseZ_L1",    _gridBaseZ_L1);
        shader.SetFloat("uGIProbeSpacingL1", PROBE_SPACING_L1);

        // L2
        shader.SetInt("uGIGridBaseX_L2",    _gridBaseX_L2);
        shader.SetInt("uGIGridBaseY_L2",    _gridBaseY_L2);
        shader.SetInt("uGIGridBaseZ_L2",    _gridBaseZ_L2);
        shader.SetFloat("uGIProbeSpacingL2", PROBE_SPACING_L2);

        // Общие (одинаковые для всех уровней)
        shader.SetInt("uGIProbeX", PROBE_X);
        shader.SetInt("uGIProbeY", PROBE_Y);
        shader.SetInt("uGIProbeZ", PROBE_Z);
    }

    // ─────────────────────────────────────────────────────────
    public void DrawProbeDebug(LineRenderer lineRenderer, Vector3 cameraPos)
    {
        int lod = GameSettings.GIDebugLOD;

        if (lod == 0 || lod == -1)
            DrawLevelDebug(lineRenderer, cameraPos, ProbePositionSsbo,
                ProbeIrradianceSsbo, PROBE_SPACING_L0, 24.0f);

        if (lod == 1 || lod == -1)
            DrawLevelDebug(lineRenderer, cameraPos, ProbePositionSsboL1,
                ProbeIrradianceSsboL1, PROBE_SPACING_L1, 80.0f);

        if (lod == 2 || lod == -1)
            DrawLevelDebug(lineRenderer, cameraPos, ProbePositionSsboL2,
                ProbeIrradianceSsboL2, PROBE_SPACING_L2, 320.0f);
    }

    private static void DrawLevelDebug(LineRenderer lr, Vector3 camPos,
                                       int posSSBO, int irrSSBO,
                                       float spacing, float drawRadius)
    {
        if (posSSBO == 0) return;

        // Читаем позиции (ProbeData = 2×vec4 = 8 float на зонд)
        float[] posData = new float[PROBE_COUNT * 8];
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, posSSBO);
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
                            posData.Length * sizeof(float), posData);

        // Читаем irradiance (12 float на зонд — SH коэффициенты)
        float[] irrData = new float[PROBE_COUNT * 12];
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, irrSSBO);
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
                            irrData.Length * sizeof(float), irrData);

        float r2  = drawRadius * drawRadius;
        float box = spacing * 0.12f;

        for (int i = 0; i < PROBE_COUNT; i++)
        {
            // stride = 8: [0..2]=pos.xyz, [3]=pos.w (alive), [4..7]=colorAndState
            float px    = posData[i * 8 + 0];
            if (px <= -9000.0f) continue;          // неинициализированный
            float py    = posData[i * 8 + 1];
            float pz    = posData[i * 8 + 2];
            float alive = posData[i * 8 + 3];

            if (alive < 0.5f) continue;            // мёртвые зонды не рисуем

            Vector3 pos = new(px, py, pz);
            if ((pos - camPos).LengthSquared > r2) continue;

            // Цвет зонда: средний цвет из SH DC-коэффициентов (band 0, коэф 0/4/8)
            // SH[0]=R_dc, SH[4]=G_dc, SH[8]=B_dc
            int b = i * 12;
            float cr = irrData[b + 0] * 0.282095f * 3.14159265f;
            float cg = irrData[b + 4] * 0.282095f * 3.14159265f;
            float cb = irrData[b + 8] * 0.282095f * 3.14159265f;

            // Нормируем к видимому диапазону (зонды могут быть тёмными или яркими)
            float maxC = MathF.Max(MathF.Max(cr, cg), MathF.Max(cb, 0.001f));
            Vector3 col = new(
                Math.Clamp(cr / maxC, 0f, 1f),
                Math.Clamp(cg / maxC, 0f, 1f),
                Math.Clamp(cb / maxC, 0f, 1f)
            );

            // Яркость куба = насколько зонд вообще освещён
            float brightness = Math.Clamp(maxC * 0.5f, 0.2f, 1.0f);
            col *= brightness;

            lr.DrawBox(pos - new Vector3(box), pos + new Vector3(box), col);
        }
    }

    // ─────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ProbePositionSsbo    != 0) GL.DeleteBuffer(ProbePositionSsbo);
        if (ProbeIrradianceSsbo  != 0) GL.DeleteBuffer(ProbeIrradianceSsbo);
        if (ProbePositionSsboL1  != 0) GL.DeleteBuffer(ProbePositionSsboL1);
        if (ProbeIrradianceSsboL1 != 0) GL.DeleteBuffer(ProbeIrradianceSsboL1);
        if (ProbePositionSsboL2  != 0) GL.DeleteBuffer(ProbePositionSsboL2);
        if (ProbeIrradianceSsboL2 != 0) GL.DeleteBuffer(ProbeIrradianceSsboL2);
    }
}