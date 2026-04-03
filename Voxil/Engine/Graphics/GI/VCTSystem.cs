// --- VCTSystem.cs ---
// Anisotropic VCT + физически корректное освещение.
//
// ИЗМЕНЕНИЯ относительно предыдущей версии:
//   + Параметр SunIntensity (по умолчанию 3.0) — художественный контроль
//     интенсивности солнца; передаётся в шейдер как uniform float uSunIntensity.
//   + Комментарий про SLICES_PER_FRAME и скорость сходимости GI.

using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;

public class VCTSystem : IDisposable
{
    // =====================================================================
    // Public constants
    // =====================================================================

    public const int CLIPMAP_SIZE = 64;

    public const float CELL_L0 = 2.0f;
    public const float CELL_L1 = 8.0f;
    public const float CELL_L2 = 32.0f;

    // Сколько Z-слайсов обновляется за кадр.
    // Полный обход клипмапа = CLIPMAP_SIZE / SLICES_PER_FRAME кадров:
    //   = 4  → 16 кадров (~0.27 с при 60fps) — меньше нагрузка на GPU
    //   = 8  → 8 кадров  (~0.13 с) — быстрее сходится GI между комнатами
    //   = 16 → 4 кадра   — максимальная скорость, но существенная нагрузка
    // Если multi-bounce GI медленно "заполняет" комнаты, увеличьте это значение.
    private const int SLICES_PER_FRAME = 4;

    // =====================================================================
    // GPU textures — radiance
    // =====================================================================

    public int ClipmapL0 { get; private set; }
    public int ClipmapL1 { get; private set; }
    public int ClipmapL2 { get; private set; }

    // =====================================================================
    // GPU textures — анизотропная opacity (r=op_x, g=op_y, b=op_z)
    // =====================================================================

    public int AnisoL0 { get; private set; }
    public int AnisoL1 { get; private set; }
    public int AnisoL2 { get; private set; }

    // =====================================================================
    // Художественные параметры (можно менять во время игры)
    // =====================================================================

    /// <summary>
    /// Интенсивность солнца в условных единицах.
    /// Типичные значения: 2.0 (пасмурный день) ... 5.0 (яркий тропический день).
    /// </summary>
    public float SunIntensity { get; set; } = 3.0f;

    // =====================================================================
    // Private state
    // =====================================================================

    private readonly Shader _buildShader;
    private bool _disposed;

    private Vector3i _originL0;
    private Vector3i _originL1;
    private Vector3i _originL2;

    private int _currentSlice;

    // =====================================================================
    // Construction
    // =====================================================================

    public VCTSystem(Shader buildShader)
    {
        _buildShader = buildShader ?? throw new ArgumentNullException(nameof(buildShader));

        ClipmapL0 = CreateClipmapTexture();
        ClipmapL1 = CreateClipmapTexture();
        ClipmapL2 = CreateClipmapTexture();

        AnisoL0 = CreateClipmapTexture();
        AnisoL1 = CreateClipmapTexture();
        AnisoL2 = CreateClipmapTexture();

        BindImageUnits();
    }

    private static int CreateClipmapTexture()
    {
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, tex);

        GL.TexImage3D(
            TextureTarget.Texture3D, 0,
            PixelInternalFormat.Rgba16f,
            CLIPMAP_SIZE, CLIPMAP_SIZE, CLIPMAP_SIZE,
            0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero
        );

        GL.TexParameter(TextureTarget.Texture3D,
            TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D,
            TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D,
            TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture3D,
            TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture3D,
            TextureParameterName.TextureWrapR, (int)TextureWrapMode.Repeat);

        return tex;
    }

    private void BindImageUnits()
    {
        // Units 0-2: radiance
        GL.BindImageTexture(0, ClipmapL0, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
        GL.BindImageTexture(1, ClipmapL1, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
        GL.BindImageTexture(2, ClipmapL2, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);

        // Units 3-5: анизотропная opacity
        GL.BindImageTexture(3, AnisoL0, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
        GL.BindImageTexture(4, AnisoL1, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
        GL.BindImageTexture(5, AnisoL2, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
    }

    // =====================================================================
    // Per-frame update
    // =====================================================================

    public void Update(
            Vector3 camPos,
            Vector3 sunDir,
            int bMinX, int bMinY, int bMinZ,
            int bMaxX, int bMaxY, int bMaxZ,
            int maxRaySteps,
            Vector3 gridOrigin, float gridStep, int gridSize,
            int objectCount,
            int pointLightCount)
    {
        _originL0 = SnapOrigin(camPos, CELL_L0);
        _originL1 = SnapOrigin(camPos, CELL_L1);
        _originL2 = SnapOrigin(camPos, CELL_L2);

        BindImageUnits();

        _buildShader.Use();

        _buildShader.SetVector3("uCamPos", camPos);
        _buildShader.SetVector3("uSunDir", sunDir);
        _buildShader.SetInt("uBoundMinX", bMinX);
        _buildShader.SetInt("uBoundMinY", bMinY);
        _buildShader.SetInt("uBoundMinZ", bMinZ);
        _buildShader.SetInt("uBoundMaxX", bMaxX);
        _buildShader.SetInt("uBoundMaxY", bMaxY);
        _buildShader.SetInt("uBoundMaxZ", bMaxZ);
        _buildShader.SetInt("uMaxRaySteps", maxRaySteps);
        _buildShader.SetVector3("uGridOrigin", gridOrigin);
        _buildShader.SetFloat("uGridStep", gridStep);
        _buildShader.SetInt("uGridSize", gridSize);
        _buildShader.SetInt("uObjectCount", objectCount);
        _buildShader.SetInt("uPointLightCount", pointLightCount);

        // Передаём художественный параметр интенсивности солнца
        _buildShader.SetFloat("uSunIntensity", SunIntensity);

        _buildShader.SetInt("uVCTClipmapSize", CLIPMAP_SIZE);
        _buildShader.SetInt("uVCTStartSlice", _currentSlice);

        SetOriginUniforms(_buildShader, "uVCTOriginL0", _originL0);
        SetOriginUniforms(_buildShader, "uVCTOriginL1", _originL1);
        SetOriginUniforms(_buildShader, "uVCTOriginL2", _originL2);

        int groups = CLIPMAP_SIZE / 4;

        for (int level = 0; level < 3; level++)
        {
            _buildShader.SetInt("uVCTLevel", level);
            GL.DispatchCompute(groups, groups, 1);

            GL.MemoryBarrier(
                MemoryBarrierFlags.ShaderImageAccessBarrierBit |
                MemoryBarrierFlags.TextureFetchBarrierBit);
        }

        _currentSlice = (_currentSlice + SLICES_PER_FRAME) % CLIPMAP_SIZE;
    }

    // =====================================================================
    // Sampling uniforms
    // =====================================================================

    public void SetSamplingUniforms(Shader shader)
    {
        if (shader == null) return;

        // Radiance — units 20-22
        GL.ActiveTexture(TextureUnit.Texture20);
        GL.BindTexture(TextureTarget.Texture3D, ClipmapL0);
        shader.SetInt("uVCTClipmapL0", 20);

        GL.ActiveTexture(TextureUnit.Texture21);
        GL.BindTexture(TextureTarget.Texture3D, ClipmapL1);
        shader.SetInt("uVCTClipmapL1", 21);

        GL.ActiveTexture(TextureUnit.Texture22);
        GL.BindTexture(TextureTarget.Texture3D, ClipmapL2);
        shader.SetInt("uVCTClipmapL2", 22);

        // Анизотропная opacity — units 23-25
        GL.ActiveTexture(TextureUnit.Texture23);
        GL.BindTexture(TextureTarget.Texture3D, AnisoL0);
        shader.SetInt("uVCTAnisoL0", 23);

        GL.ActiveTexture(TextureUnit.Texture24);
        GL.BindTexture(TextureTarget.Texture3D, AnisoL1);
        shader.SetInt("uVCTAnisoL1", 24);

        GL.ActiveTexture(TextureUnit.Texture25);
        GL.BindTexture(TextureTarget.Texture3D, AnisoL2);
        shader.SetInt("uVCTAnisoL2", 25);

        SetOriginUniforms(shader, "uVCTOriginL0", _originL0);
        SetOriginUniforms(shader, "uVCTOriginL1", _originL1);
        SetOriginUniforms(shader, "uVCTOriginL2", _originL2);

        shader.SetInt("uVCTClipmapSize", CLIPMAP_SIZE);
    }

    // =====================================================================
    // Debug
    // =====================================================================

    public string GetDebugInfo()
    {
        long bytesPerTex = (long)CLIPMAP_SIZE * CLIPMAP_SIZE * CLIPMAP_SIZE * 4 * 2;
        long totalMb = bytesPerTex * 6 / (1024 * 1024); // 3 radiance + 3 aniso

        int refreshFrames = CLIPMAP_SIZE / SLICES_PER_FRAME;

        return $"AVCT | Size: {CLIPMAP_SIZE}^3 ×3 | VRAM: {totalMb} MB | "
             + $"L0={CELL_L0}m L1={CELL_L1}m L2={CELL_L2}m/tx | "
             + $"Slice: {_currentSlice}/{CLIPMAP_SIZE} | "
             + $"Refresh: {refreshFrames} frames | Sun: {SunIntensity:F1}";
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static Vector3i SnapOrigin(Vector3 camPos, float cellSize) =>
        new Vector3i(
            (int)MathF.Floor(camPos.X / cellSize) - CLIPMAP_SIZE / 2,
            (int)MathF.Floor(camPos.Y / cellSize) - CLIPMAP_SIZE / 2,
            (int)MathF.Floor(camPos.Z / cellSize) - CLIPMAP_SIZE / 2
        );

    private static void SetOriginUniforms(Shader shader, string name, Vector3i v)
    {
        shader.SetInt(name + "X", v.X);
        shader.SetInt(name + "Y", v.Y);
        shader.SetInt(name + "Z", v.Z);
    }

    // =====================================================================
    // Disposal
    // =====================================================================

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (ClipmapL0 != 0) { GL.DeleteTexture(ClipmapL0); ClipmapL0 = 0; }
        if (ClipmapL1 != 0) { GL.DeleteTexture(ClipmapL1); ClipmapL1 = 0; }
        if (ClipmapL2 != 0) { GL.DeleteTexture(ClipmapL2); ClipmapL2 = 0; }
        if (AnisoL0 != 0) { GL.DeleteTexture(AnisoL0); AnisoL0 = 0; }
        if (AnisoL1 != 0) { GL.DeleteTexture(AnisoL1); AnisoL1 = 0; }
        if (AnisoL2 != 0) { GL.DeleteTexture(AnisoL2); AnisoL2 = 0; }
    }
}