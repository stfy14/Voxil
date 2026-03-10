// --- Engine/Graphics/GI/GIProbeSystem.cs ---
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;

public class GIProbeSystem : IDisposable
{
    public const int PROBE_X = 16;
    public const int PROBE_Y = 8;
    public const int PROBE_Z = 16;
    public const int PROBE_COUNT = PROBE_X * PROBE_Y * PROBE_Z;
    public const float PROBE_SPACING = 4.0f;

    public const int RAYS_PER_PROBE = 64;
    public const int PROBES_PER_FRAME = 128; 

    public const int IRRADIANCE_PROBE_SIZE = 8;  
    public const int DEPTH_PROBE_SIZE = 16;      

    public readonly int IrradianceAtlasWidth = PROBE_X * IRRADIANCE_PROBE_SIZE;
    public readonly int IrradianceAtlasHeight = (PROBE_Y * PROBE_Z) * IRRADIANCE_PROBE_SIZE;
    
    public readonly int DepthAtlasWidth = PROBE_X * DEPTH_PROBE_SIZE;
    public readonly int DepthAtlasHeight = (PROBE_Y * PROBE_Z) * DEPTH_PROBE_SIZE;

    public int ProbePositionSsbo { get; private set; }
    public int IrradianceAtlasTex { get; private set; }
    public int DepthAtlasTex { get; private set; }

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
        // ИСПРАВЛЕНИЕ: Теперь структура занимает 8 float-ов (32 байта) на зонд
        float[] emptyPos = new float[PROBE_COUNT * 8];
        for (int i = 0; i < PROBE_COUNT; i++)
        {
            emptyPos[i * 8 + 0] = -9999.0f; // pos.x
            emptyPos[i * 8 + 1] = -9999.0f; // pos.y
            emptyPos[i * 8 + 2] = -9999.0f; // pos.z
        }

        ProbePositionSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ProbePositionSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, PROBE_COUNT * 8 * sizeof(float), emptyPos, BufferUsageHint.DynamicDraw);

        IrradianceAtlasTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, IrradianceAtlasTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, IrradianceAtlasWidth, IrradianceAtlasHeight, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        DepthAtlasTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, DepthAtlasTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rg16f, DepthAtlasWidth, DepthAtlasHeight, 0, PixelFormat.Rg, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

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
        _updateShader.SetInt("uRaysPerProbe",    RAYS_PER_PROBE);
        _updateShader.SetFloat("uTime",          time);
        _updateShader.SetVector3("uSunDir",      sunDir);
        _updateShader.SetFloat("uProbeSpacing",  PROBE_SPACING);
        
        _updateShader.SetInt("uProbeGridX", PROBE_X);
        _updateShader.SetInt("uProbeGridY", PROBE_Y);
        _updateShader.SetInt("uProbeGridZ", PROBE_Z);
        _updateShader.SetInt("uIrradianceSize", IRRADIANCE_PROBE_SIZE);
        _updateShader.SetInt("uDepthSize", DEPTH_PROBE_SIZE);

        _updateShader.SetInt("uBoundMinX", boundMinX);
        _updateShader.SetInt("uBoundMinY", boundMinY);
        _updateShader.SetInt("uBoundMinZ", boundMinZ);
        _updateShader.SetInt("uBoundMaxX", boundMaxX);
        _updateShader.SetInt("uBoundMaxY", boundMaxY);
        _updateShader.SetInt("uBoundMaxZ", boundMaxZ);
        _updateShader.SetInt("uMaxRaySteps", maxRaySteps / 2);

        GL.DispatchCompute(PROBES_PER_FRAME, RAYS_PER_PROBE, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);

        _updateCursor = (_updateCursor + PROBES_PER_FRAME) % PROBE_COUNT;
    }

    public void Bind()
    {
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, ProbePositionSsbo);
        GL.BindImageTexture(2, IrradianceAtlasTex, 0, false, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
        GL.BindImageTexture(3, DepthAtlasTex, 0, false, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rg16f);
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
        shader.SetInt("uIrradianceSize", IRRADIANCE_PROBE_SIZE);
        shader.SetInt("uDepthSize",      DEPTH_PROBE_SIZE);
    }

    // ИСПРАВЛЕНИЕ: Читаем с видеокарты и рисуем маленькие кубики!
    public void DrawProbeDebug(LineRenderer lineRenderer, Vector3 cameraPos)
    {
        if (ProbePositionSsbo == 0) return;

        float[] probeData = new float[PROBE_COUNT * 8];
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ProbePositionSsbo);
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, probeData.Length * sizeof(float), probeData);

        float boxRadius = 0.08f; 

        for (int i = 0; i < PROBE_COUNT; i++)
        {
            float px = probeData[i * 8 + 0];
            if (px <= -9000.0f) continue; 

            float py = probeData[i * 8 + 1];
            float pz = probeData[i * 8 + 2];
            
            float cr = probeData[i * 8 + 4];
            float cg = probeData[i * 8 + 5];
            float cb = probeData[i * 8 + 6];
            float state = probeData[i * 8 + 7];

            // Координаты уже приходят смещенными из шейдера, так что они идеально совпадут
            Vector3 pos = new Vector3(px, py, pz);
            
            if ((pos - cameraPos).LengthSquared > 25.0f * 25.0f) continue;

            Vector3 color;

            if (state < 0.5f) 
            {
                color = new Vector3(0.25f, 0.25f, 0.25f);
            } 
            else 
            {
                color = new Vector3(cr, cg, cb);
                color *= 1.5f; 
                
                color.X = Math.Clamp(color.X, 0.1f, 1.0f);
                color.Y = Math.Clamp(color.Y, 0.1f, 1.0f);
                color.Z = Math.Clamp(color.Z, 0.1f, 1.0f);
            }

            lineRenderer.DrawBox(pos - new Vector3(boxRadius), pos + new Vector3(boxRadius), color);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ProbePositionSsbo != 0) GL.DeleteBuffer(ProbePositionSsbo);
        if (IrradianceAtlasTex != 0) GL.DeleteTexture(IrradianceAtlasTex);
        if (DepthAtlasTex != 0) GL.DeleteTexture(DepthAtlasTex);
    }
}