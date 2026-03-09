// --- Engine/Graphics/GI/GIProbeSystem.cs ---
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;

public class GIProbeSystem : IDisposable
{
    public const int PROBE_X = 8;
    public const int PROBE_Y = 4;
    public const int PROBE_Z = 8;
    public const int PROBE_COUNT = PROBE_X * PROBE_Y * PROBE_Z;
    public const float PROBE_SPACING = 4.0f;

    public const int SH_FLOATS_PER_PROBE = 12;
    public const int RAYS_PER_PROBE = 64;
    public const int PROBES_PER_FRAME = 16; 

    public const int BINDING_PROBE_POSITIONS = 16;
    public const int BINDING_PROBE_IRRADIANCE = 17;

    private int _probePositionSsbo;  
    private int _probeIrradianceSsbo; 

    // Храним БАЗОВЫЙ индекс сетки, а не float-координаты
    private int _gridBaseX, _gridBaseY, _gridBaseZ;
    private int _updateCursor = 0; 
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
        // Инициализируем позиции значением -9999, чтобы в 1 кадре они все считались "грязными" и мгновенно обновились
        float[] emptyPos = new float[PROBE_COUNT * 4];
        Array.Fill(emptyPos, -9999.0f);

        _probePositionSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _probePositionSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, PROBE_COUNT * 4 * sizeof(float), emptyPos, BufferUsageHint.DynamicDraw);

        _probeIrradianceSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _probeIrradianceSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, PROBE_COUNT * SH_FLOATS_PER_PROBE * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);

        Bind();
    }

    public void Update(Vector3 cameraPosition, Vector3 sunDir, float time,
                       int boundMinX, int boundMinY, int boundMinZ,
                       int boundMaxX, int boundMaxY, int boundMaxZ,
                       int maxRaySteps)
    {
        if (!IsValid) return;
        Bind();

        // Вычисляем базовый индекс сетки сдвига (Toroidal Base)
        float floatBaseX = (cameraPosition.X / PROBE_SPACING) - (PROBE_X * 0.5f);
        float floatBaseY = (cameraPosition.Y / PROBE_SPACING) - (PROBE_Y * 0.5f);
        float floatBaseZ = (cameraPosition.Z / PROBE_SPACING) - (PROBE_Z * 0.5f);

        _gridBaseX = (int)Math.Floor(floatBaseX);
        _gridBaseY = (int)Math.Floor(floatBaseY);
        _gridBaseZ = (int)Math.Floor(floatBaseZ);

        _updateShader.Use();
        // Передаем координаты сетки
        _updateShader.SetInt("uGIGridBaseX", _gridBaseX);
        _updateShader.SetInt("uGIGridBaseY", _gridBaseY);
        _updateShader.SetInt("uGIGridBaseZ", _gridBaseZ);

        _updateShader.SetInt("uProbeStartIndex", _updateCursor);
        _updateShader.SetInt("uProbesThisFrame", PROBES_PER_FRAME);
        _updateShader.SetInt("uRaysPerProbe",    RAYS_PER_PROBE);
        _updateShader.SetFloat("uTime",          time);
        _updateShader.SetVector3("uSunDir",      sunDir);
        _updateShader.SetFloat("uProbeSpacing",  PROBE_SPACING);
        _updateShader.SetInt("uProbeGridX",      PROBE_X);
        _updateShader.SetInt("uProbeGridY",      PROBE_Y);
        _updateShader.SetInt("uProbeGridZ",      PROBE_Z);
        _updateShader.SetInt("uBoundMinX",       boundMinX);
        _updateShader.SetInt("uBoundMinY",       boundMinY);
        _updateShader.SetInt("uBoundMinZ",       boundMinZ);
        _updateShader.SetInt("uBoundMaxX",       boundMaxX);
        _updateShader.SetInt("uBoundMaxY",       boundMaxY);
        _updateShader.SetInt("uBoundMaxZ",       boundMaxZ);
        _updateShader.SetInt("uMaxRaySteps",     maxRaySteps / 2);

        GL.DispatchCompute(PROBES_PER_FRAME, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        _updateCursor = (_updateCursor + PROBES_PER_FRAME) % PROBE_COUNT;
    }

    public void Bind()
    {
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_PROBE_POSITIONS,  _probePositionSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_PROBE_IRRADIANCE, _probeIrradianceSsbo);
    }

    public void SetSamplingUniforms(Shader shader)
    {
        if (shader == null) return;
        shader.SetInt("uGIGridBaseX",    _gridBaseX);
        shader.SetInt("uGIGridBaseY",    _gridBaseY);
        shader.SetInt("uGIGridBaseZ",    _gridBaseZ);
        shader.SetFloat("uGIProbeSpacing", PROBE_SPACING);
        shader.SetInt("uGIProbeX",       PROBE_X);
        shader.SetInt("uGIProbeY",       PROBE_Y);
        shader.SetInt("uGIProbeZ",       PROBE_Z);
    }

    public void DrawProbeDebug(LineRenderer lineRenderer, Vector3 cameraPos)
    {
        var yellow = new Vector3(1.0f, 0.85f, 0.1f);
        var dim    = new Vector3(0.4f, 0.35f, 0.05f);
        float nearDist = PROBE_SPACING * 2.0f;

        // Рисуем сетку на основе базовых координат (без Toroidal смешивания, чисто визуал)
        for (int z = 0; z < PROBE_Z; z++)
        for (int y = 0; y < PROBE_Y; y++)
        for (int x = 0; x < PROBE_X; x++)
        {
            Vector3 pos = new Vector3(_gridBaseX + x, _gridBaseY + y, _gridBaseZ + z) * PROBE_SPACING;
            float dist = (pos - cameraPos).Length;
            lineRenderer.DrawPoint(pos, dist < nearDist ? 0.3f : 0.15f, dist < nearDist ? yellow : dim);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_probePositionSsbo  != 0) GL.DeleteBuffer(_probePositionSsbo);
        if (_probeIrradianceSsbo != 0) GL.DeleteBuffer(_probeIrradianceSsbo);
    }
}