// --- Engine/Graphics/GI/GIProbeSystem.cs ---
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

/// <summary>
/// Управляет сеткой irradiance-зондов для GI.
/// Зонды обновляются инкрементально: каждый кадр обновляется PROBES_PER_FRAME зондов.
/// Каждый зонд хранит SH L1 (12 float = 4 коэф. × 3 канала RGB).
/// </summary>
public class GIProbeSystem : IDisposable
{
    // --- Константы сетки ---
    public const int PROBE_X        = 8;
    public const int PROBE_Y        = 4;
    public const int PROBE_Z        = 8;
    public const int PROBE_COUNT    = PROBE_X * PROBE_Y * PROBE_Z; // 256
    public const float PROBE_SPACING = 4.0f; // метры между зондами

    // --- SH L1: 4 коэф × 3 канала = 12 float на зонд ---
    public const int SH_FLOATS_PER_PROBE = 12;
    public const int RAYS_PER_PROBE      = 64;
    public const int PROBES_PER_FRAME    = 16; // Обновляем по 16 зондов за кадр → полный цикл за 16 кадров

    // --- Биндинги SSBO ---
    public const int BINDING_PROBE_POSITIONS   = 16;
    public const int BINDING_PROBE_IRRADIANCE  = 17;

    // --- GPU буферы ---
    private int _probePositionSsbo;  // vec4[PROBE_COUNT] — позиции зондов
    private int _probeIrradianceSsbo; // float[PROBE_COUNT * 12] — SH данные

    // --- Состояние ---
    private Vector3 _lastGridOrigin;
    private bool    _gridDirty  = true;
    private int     _updateCursor = 0; // Следующий зонд для обновления

    // --- Шейдер ---
    private readonly Shader _updateShader;

    // --- CPU копия позиций для загрузки ---
    private readonly float[] _probePositions = new float[PROBE_COUNT * 4]; // vec4[]

    private bool _disposed;

    public bool IsValid => _updateShader != null;

    // Центр сетки зондов (обновляется при смещении игрока)
    public Vector3 GridOrigin => _lastGridOrigin;
    public int     ProbeX     => PROBE_X;
    public int     ProbeY     => PROBE_Y;
    public int     ProbeZ     => PROBE_Z;
    public float   ProbeSpacing => PROBE_SPACING;

    public GIProbeSystem(Shader probeUpdateShader)
    {
        _updateShader = probeUpdateShader;
        InitBuffers();
    }

    private void InitBuffers()
    {
        // SSBO для позиций зондов
        _probePositionSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _probePositionSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer,
            PROBE_COUNT * 4 * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_PROBE_POSITIONS, _probePositionSsbo);

        // SSBO для SH данных — инициализируем нулями
        _probeIrradianceSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _probeIrradianceSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer,
            PROBE_COUNT * SH_FLOATS_PER_PROBE * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_PROBE_IRRADIANCE, _probeIrradianceSsbo);

        Console.WriteLine($"[GI] Probes initialized: {PROBE_COUNT} probes, SH L1, {PROBE_COUNT * SH_FLOATS_PER_PROBE * 4 / 1024} KB VRAM");
    }

    /// <summary>
    /// Вызывается каждый кадр. Если камера далеко ушла — пересчитываем сетку.
    /// Затем обновляем PROBES_PER_FRAME зондов через compute shader.
    /// </summary>
    public void Update(Vector3 cameraPosition, Vector3 sunDir, float time,
                       int boundMinX, int boundMinY, int boundMinZ,
                       int boundMaxX, int boundMaxY, int boundMaxZ,
                       int maxRaySteps)
    {
        if (!IsValid) return;
        Bind();

        Vector3 desiredOrigin = SnapGridOrigin(cameraPosition);
        bool gridMoved = _gridDirty || Vector3.DistanceSquared(desiredOrigin, _lastGridOrigin) > PROBE_SPACING * PROBE_SPACING;
    
        if (gridMoved)
        {
            _lastGridOrigin = desiredOrigin;
            RebuildProbePositions();
            _updateCursor = 0;
            _gridDirty = false;
        }

        int countThisFrame = gridMoved ? PROBE_COUNT : Math.Min(PROBES_PER_FRAME, PROBE_COUNT - _updateCursor);
        float blendFactor  = gridMoved ? 1.0f : 0.15f; // 1.0 - мгновенная перезапись, 0.15 - плавное накопление

        _updateShader.Use();
        _updateShader.SetFloat("uBlendFactor", blendFactor); // Добавь этот uniform в шейдер
        _updateShader.SetInt("uProbeStartIndex", _updateCursor);
        _updateShader.SetInt("uProbesThisFrame", countThisFrame);
        _updateShader.SetInt("uRaysPerProbe",    RAYS_PER_PROBE);
        _updateShader.SetFloat("uTime",          time);
        _updateShader.SetVector3("uSunDir",      sunDir);
        _updateShader.SetVector3("uGridOrigin",  _lastGridOrigin);
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
        _updateShader.SetInt("uMaxRaySteps",     maxRaySteps / 2); // Меньше шагов для зондов

        GL.DispatchCompute(countThisFrame, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

        if (!gridMoved) {
            _updateCursor = (_updateCursor + PROBES_PER_FRAME) % PROBE_COUNT;
        }
    }

    /// <summary>Привязывает SSBO зондов к нужным слотам.</summary>
    public void Bind()
    {
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_PROBE_POSITIONS,  _probePositionSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_PROBE_IRRADIANCE, _probeIrradianceSsbo);
    }

    /// <summary>Устанавливает uniforms для sampling в composite шейдере.</summary>
    public void SetSamplingUniforms(Shader shader)
    {
        if (shader == null) return;
        shader.SetVector3("uGIGridOrigin",   _lastGridOrigin);
        shader.SetFloat  ("uGIProbeSpacing", PROBE_SPACING);
        shader.SetInt    ("uGIProbeX",       PROBE_X);
        shader.SetInt    ("uGIProbeY",       PROBE_Y);
        shader.SetInt    ("uGIProbeZ",       PROBE_Z);
    }

    private void RebuildProbePositions()
    {
        int idx = 0;
        for (int z = 0; z < PROBE_Z; z++)
        for (int y = 0; y < PROBE_Y; y++)
        for (int x = 0; x < PROBE_X; x++)
        {
            float wx = _lastGridOrigin.X + (x - PROBE_X * 0.5f + 0.5f) * PROBE_SPACING;
            float wy = _lastGridOrigin.Y + (y - PROBE_Y * 0.5f + 0.5f) * PROBE_SPACING;
            float wz = _lastGridOrigin.Z + (z - PROBE_Z * 0.5f + 0.5f) * PROBE_SPACING;

            _probePositions[idx * 4 + 0] = wx;
            _probePositions[idx * 4 + 1] = wy;
            _probePositions[idx * 4 + 2] = wz;
            _probePositions[idx * 4 + 3] = (float)idx; // w = probe index
            idx++;
        }

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _probePositionSsbo);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
            PROBE_COUNT * 4 * sizeof(float), _probePositions);

        Console.WriteLine($"[GI] Probe grid rebuilt at {_lastGridOrigin}");
    }

    private Vector3 SnapGridOrigin(Vector3 camPos)
    {
        // Снэпим к целым клеткам PROBE_SPACING
        float s = PROBE_SPACING;
        return new Vector3(
            (float)Math.Floor(camPos.X / s) * s,
            (float)Math.Floor(camPos.Y / s) * s,
            (float)Math.Floor(camPos.Z / s) * s);
    }

    public void ForceFullUpdate() { _updateCursor = 0; _gridDirty = true; }

    /// <summary>
    /// Рисует позиции зондов через LineRenderer (вызывается из GameScene.Render когда ShowGIProbes=true).
    /// Каждый зонд — маленький крест, ближайшие к камере — ярче.
    /// </summary>
    public void DrawProbeDebug(LineRenderer lineRenderer, Vector3 cameraPos)
    {
        var yellow = new Vector3(1.0f, 0.85f, 0.1f);
        var dim    = new Vector3(0.4f, 0.35f, 0.05f);
        float nearDist = PROBE_SPACING * 2.0f;

        for (int i = 0; i < PROBE_COUNT; i++)
        {
            float px = _probePositions[i * 4 + 0];
            float py = _probePositions[i * 4 + 1];
            float pz = _probePositions[i * 4 + 2];
            var pos = new Vector3(px, py, pz);

            float dist = (pos - cameraPos).Length;
            var color = dist < nearDist ? yellow : dim;
            float size = dist < nearDist ? 0.3f : 0.15f;

            lineRenderer.DrawPoint(pos, size, color);
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