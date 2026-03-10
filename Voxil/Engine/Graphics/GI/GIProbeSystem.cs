// --- Engine/Graphics/GI/GIProbeSystem.cs ---
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;

public class GIProbeSystem : IDisposable
{
    // ИСПРАВЛЕНИЕ: Шаг зондов = 1 метр! (Один зонд на каждый блок)
    public const float PROBE_SPACING = 1.0f;
    
    // Сетка: 32х16х32 = 16 384 зонда. Хватит с головой.
    public const int PROBE_X = 32;
    public const int PROBE_Y = 16;
    public const int PROBE_Z = 32;
    public const int PROBE_COUNT = PROBE_X * PROBE_Y * PROBE_Z;

    public const int RAYS_PER_PROBE = 64;
    // Обновляем 1024 зонда за кадр (вся сетка обновится за 16 кадров - очень плавно)
    public const int PROBES_PER_FRAME = 1024;

    public int ProbePositionSsbo { get; private set; }
    public int ProbeIrradianceSsbo { get; private set; }

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
        float[] emptyPos = new float[PROBE_COUNT * 8];
        Array.Fill(emptyPos, -9999.0f);

        ProbePositionSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ProbePositionSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, emptyPos.Length * sizeof(float), emptyPos, BufferUsageHint.DynamicDraw);

        float[] emptySH = new float[PROBE_COUNT * 12];
        ProbeIrradianceSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ProbeIrradianceSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, emptySH.Length * sizeof(float), emptySH, BufferUsageHint.DynamicDraw);

        Bind();
    }

    public void Update(Vector3 cameraPosition, Vector3 sunDir, float time,
                       int boundMinX, int boundMinY, int boundMinZ,
                       int boundMaxX, int boundMaxY, int boundMaxZ,
                       int maxRaySteps)
    {
        if (!IsValid) return;
        Bind();

        float floatBaseX = (cameraPosition.X / PROBE_SPACING) - (PROBE_X * 0.5f);
        float floatBaseY = (cameraPosition.Y / PROBE_SPACING) - (PROBE_Y * 0.5f);
        float floatBaseZ = (cameraPosition.Z / PROBE_SPACING) - (PROBE_Z * 0.5f);

        _gridBaseX = (int)Math.Floor(floatBaseX);
        _gridBaseY = (int)Math.Floor(floatBaseY);
        _gridBaseZ = (int)Math.Floor(floatBaseZ);

        _updateShader.Use();
        _updateShader.SetInt("uGIGridBaseX", _gridBaseX);
        _updateShader.SetInt("uGIGridBaseY", _gridBaseY);
        _updateShader.SetInt("uGIGridBaseZ", _gridBaseZ);

        _updateShader.SetInt("uProbeStartIndex", _updateCursor);
        _updateShader.SetInt("uProbesThisFrame", PROBES_PER_FRAME);
        _updateShader.SetInt("uRaysPerProbe", RAYS_PER_PROBE);
        _updateShader.SetFloat("uTime", time);
        _updateShader.SetVector3("uSunDir", sunDir);
        _updateShader.SetFloat("uProbeSpacing", PROBE_SPACING);

        _updateShader.SetInt("uProbeGridX", PROBE_X);
        _updateShader.SetInt("uProbeGridY", PROBE_Y);
        _updateShader.SetInt("uProbeGridZ", PROBE_Z);

        _updateShader.SetInt("uBoundMinX", boundMinX);
        _updateShader.SetInt("uBoundMinY", boundMinY);
        _updateShader.SetInt("uBoundMinZ", boundMinZ);
        _updateShader.SetInt("uBoundMaxX", boundMaxX);
        _updateShader.SetInt("uBoundMaxY", boundMaxY);
        _updateShader.SetInt("uBoundMaxZ", boundMaxZ);
        _updateShader.SetInt("uMaxRaySteps", maxRaySteps / 2);

        // ИСПРАВЛЕНИЕ: Безопасный диспатч группами по 64 потока
        GL.DispatchCompute((PROBES_PER_FRAME + 63) / 64, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        _updateCursor = (_updateCursor + PROBES_PER_FRAME) % PROBE_COUNT;
    }

    public void Bind()
    {
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, ProbePositionSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 17, ProbeIrradianceSsbo);
    }

    public void SetSamplingUniforms(Shader shader)
    {
        if (shader == null) return;
        shader.SetInt("uGIGridBaseX", _gridBaseX);
        shader.SetInt("uGIGridBaseY", _gridBaseY);
        shader.SetInt("uGIGridBaseZ", _gridBaseZ);
        shader.SetFloat("uGIProbeSpacing", PROBE_SPACING);
        shader.SetInt("uGIProbeX", PROBE_X);
        shader.SetInt("uGIProbeY", PROBE_Y);
        shader.SetInt("uGIProbeZ", PROBE_Z);
    }

    public void DrawProbeDebug(LineRenderer lineRenderer, Vector3 cameraPos)
    {
        if (ProbePositionSsbo == 0) return;

        float[] probeData = new float[PROBE_COUNT * 8];
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ProbePositionSsbo);
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, probeData.Length * sizeof(float), probeData);

        float boxRadius = 0.05f; 

        for (int i = 0; i < PROBE_COUNT; i++)
        {
            float px    = probeData[i * 8 + 0];
            if (px <= -9000.0f) continue; 
            float py    = probeData[i * 8 + 1];
            float pz    = probeData[i * 8 + 2];
            float state = probeData[i * 8 + 3]; // pos.w

            Vector3 pos = new Vector3(px, py, pz);
            
            // Чтобы не лагало, рисуем дебаг только в радиусе 12 метров
            if ((pos - cameraPos).LengthSquared > 12.0f * 12.0f) continue;

            // Серый цвет для мертвых зондов, желтый для живых
            Vector3 color = (state < 0.5f) ? new Vector3(0.2f, 0.2f, 0.2f) : new Vector3(0.9f, 0.8f, 0.2f);
            lineRenderer.DrawBox(pos - new Vector3(boxRadius), pos + new Vector3(boxRadius), color);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ProbePositionSsbo != 0) GL.DeleteBuffer(ProbePositionSsbo);
        if (ProbeIrradianceSsbo != 0) GL.DeleteBuffer(ProbeIrradianceSsbo);
    }
}