// --- VCTSystem.cs ---
// Manages the Voxel Cone Tracing clipmap: 3 cascades of rgba16f 3D textures.
// Replaces GIProbeSystem entirely.
//
// Per-frame responsibilities:
//   1. Update clipmap origins to follow the camera (scrolling window)
//   2. Dispatch vct_clipmap_build.comp to rebuild 4 Z-slices of each cascade
//   3. Expose textures + uniforms to the fragment/lighting shader
//
// Typical usage in GpuRaycastingRenderer:
//   _vctSystem.Update(camPos, sunDir, ...uniforms...);
//   _vctSystem.SetSamplingUniforms(shadowShader);
//
// The shadow/lighting shader must #include "include/vct.glsl"
// and call SampleGIVCT(worldPos, normal) instead of SampleGIProbes.

using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;

public class VCTSystem : IDisposable
{
    // =====================================================================
    // Public constants — readable from outside (UI, debug overlays, etc.)
    // =====================================================================

    public const int CLIPMAP_SIZE = 64;    // Texels per axis per cascade (power of 2!)

    public const float CELL_L0 = 2.0f;       // World metres per texel, L0
    public const float CELL_L1 = 8.0f;       // L1
    public const float CELL_L2 = 32.0f;      // L2

    // Coverage radius = CLIPMAP_SIZE * CELL / 2
    //   L0: 64m   L1: 256m   L2: 1024m
    //
    // Compare to old DDGI:
    //   L0: 10m   L1: 40m    L2: 160m
    // Caves are now covered at full resolution even 60m away.

    private const int SLICES_PER_FRAME = 4;  // Z-slices updated per dispatch
    // Full refresh: CLIPMAP_SIZE / SLICES_PER_FRAME = 16 frames

    // =====================================================================
    // GPU textures
    // =====================================================================

    public int ClipmapL0 { get; private set; }
    public int ClipmapL1 { get; private set; }
    public int ClipmapL2 { get; private set; }

    // =====================================================================
    // Private state
    // =====================================================================

    private readonly Shader _buildShader;
    private bool _disposed;

    // World texel coordinates of the minimum corner of each cascade.
    // Updated every frame to track the camera.
    private Vector3i _originL0;
    private Vector3i _originL1;
    private Vector3i _originL2;

    // Which Z-slice group to update next (cycles 0, 4, 8 ... 60, 0, ...)
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

        // Bind image units for the compute shader (fixed bindings 0-2)
        BindImageUnits();
    }

    private static int CreateClipmapTexture()
    {
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, tex);

        // rgba16f: rgb = outgoing radiance, a = opacity
        GL.TexImage3D(
            TextureTarget.Texture3D, 0,
            PixelInternalFormat.Rgba16f,
            CLIPMAP_SIZE, CLIPMAP_SIZE, CLIPMAP_SIZE,
            0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero
        );

        // Linear filtering for smooth cone interpolation
        GL.TexParameter(TextureTarget.Texture3D,
            TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D,
            TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

        // REPEAT wrap — essential for toroidal (scrolling) addressing
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
        GL.BindImageTexture(0, ClipmapL0, 0, true, 0,
            TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f); // было WriteOnly
        GL.BindImageTexture(1, ClipmapL1, 0, true, 0,
            TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
        GL.BindImageTexture(2, ClipmapL2, 0, true, 0,
            TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
    }

    // =====================================================================
    // Per-frame update
    // =====================================================================

    /// <summary>
    /// Call once per frame before the lighting pass.
    /// Updates the clipmap to track the camera and refreshes 4 Z-slices.
    /// </summary>
    public void Update(
            Vector3 camPos,
            Vector3 sunDir,
            int bMinX, int bMinY, int bMinZ,
            int bMaxX, int bMaxY, int bMaxZ,
            int maxRaySteps,
            Vector3 gridOrigin, float gridStep, int gridSize,
            int objectCount,
            int pointLightCount) // <--- ДОБАВИТЬ ЭТО
    {
        // Snap origins to grid — camera drives the scrolling window
        _originL0 = SnapOrigin(camPos, CELL_L0);
        _originL1 = SnapOrigin(camPos, CELL_L1);
        _originL2 = SnapOrigin(camPos, CELL_L2);

        // Re-bind image units (must happen before dispatch)
        BindImageUnits();

        _buildShader.Use();

        // === Common world-access uniforms ===
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

        // === VCT-specific uniforms ===
        _buildShader.SetInt("uVCTClipmapSize", CLIPMAP_SIZE);
        _buildShader.SetInt("uVCTStartSlice", _currentSlice);

        // Origins (passed as individual ints — matches engine convention)
        SetOriginUniforms(_buildShader, "uVCTOriginL0", _originL0);
        SetOriginUniforms(_buildShader, "uVCTOriginL1", _originL1);
        SetOriginUniforms(_buildShader, "uVCTOriginL2", _originL2);

        // === Dispatch once per cascade ===
        // local_size = 4x4x4, updating CLIPMAP_SIZE x CLIPMAP_SIZE x SLICES_PER_FRAME texels
        int groups = CLIPMAP_SIZE / 4; // 16

        for (int level = 0; level < 3; level++)
        {
            _buildShader.SetInt("uVCTLevel", level);
            GL.DispatchCompute(groups, groups, 1);

            // Ensure writes are visible before sampling
            GL.MemoryBarrier(
                MemoryBarrierFlags.ShaderImageAccessBarrierBit |
                MemoryBarrierFlags.TextureFetchBarrierBit);
        }

        // Advance the slice cursor — wraps every CLIPMAP_SIZE/SLICES_PER_FRAME frames
        _currentSlice = (_currentSlice + SLICES_PER_FRAME) % CLIPMAP_SIZE;
    }

    // =====================================================================
    // Sampling uniforms (set on the lighting / shadow shader)
    // =====================================================================

    /// <summary>
    /// Binds clipmap textures and uniforms for use in vct.glsl / SampleGIVCT().
    /// Call this just before the lighting pass that uses vct.glsl.
    /// </summary>
    public void SetSamplingUniforms(Shader shader)
    {
        if (shader == null) return;

        // Texture units 20-22 (well above DDGI's 10-15 range — no conflicts)
        GL.ActiveTexture(TextureUnit.Texture20);
        GL.BindTexture(TextureTarget.Texture3D, ClipmapL0);
        shader.SetInt("uVCTClipmapL0", 20);

        GL.ActiveTexture(TextureUnit.Texture21);
        GL.BindTexture(TextureTarget.Texture3D, ClipmapL1);
        shader.SetInt("uVCTClipmapL1", 21);

        GL.ActiveTexture(TextureUnit.Texture22);
        GL.BindTexture(TextureTarget.Texture3D, ClipmapL2);
        shader.SetInt("uVCTClipmapL2", 22);

        // Origins
        SetOriginUniforms(shader, "uVCTOriginL0", _originL0);
        SetOriginUniforms(shader, "uVCTOriginL1", _originL1);
        SetOriginUniforms(shader, "uVCTOriginL2", _originL2);

        shader.SetInt("uVCTClipmapSize", CLIPMAP_SIZE);
    }

    // =====================================================================
    // Debug / diagnostics
    // =====================================================================

    public string GetDebugInfo()
    {
        // 3 cascades × CLIPMAP_SIZE^3 × 4 bytes per channel × 4 channels
        long bytesPerCascade = (long)CLIPMAP_SIZE * CLIPMAP_SIZE * CLIPMAP_SIZE * 4 * 2; // rgba16f = 2 bytes/ch
        long totalMb = bytesPerCascade * 3 / (1024 * 1024);

        return $"VCT Clipmap | Size: {CLIPMAP_SIZE}^3 x3 | VRAM: {totalMb} MB | "
             + $"L0={CELL_L0}m/tx L1={CELL_L1}m/tx L2={CELL_L2}m/tx | "
             + $"Slice: {_currentSlice}/{CLIPMAP_SIZE}";
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    /// <summary>
    /// Computes the world texel coordinate of the minimum corner of a clipmap
    /// centered on the camera. Snapped to the cell grid.
    /// </summary>
    private static Vector3i SnapOrigin(Vector3 camPos, float cellSize)
    {
        return new Vector3i(
            (int)MathF.Floor(camPos.X / cellSize) - CLIPMAP_SIZE / 2,
            (int)MathF.Floor(camPos.Y / cellSize) - CLIPMAP_SIZE / 2,
            (int)MathF.Floor(camPos.Z / cellSize) - CLIPMAP_SIZE / 2
        );
    }

    /// <summary>
    /// Sets ivec3 uniform as three separate ints (engine convention from GIProbeSystem).
    /// </summary>
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
    }
}
