// --- START OF FILE ShaderSystem.cs ---

using System;
using System.Collections.Generic;

public class ShaderSystem : IDisposable
{
    public Shader RaycastShader { get; private set; }
    public event Action OnShaderReloaded;

    // Аргумент int banks больше не нужен, но оставим сигнатуру для совместимости, или можно убрать
    public void Compile(int banksCount, int chunksPerBank) // <--- Новые аргументы
    {
        RaycastShader?.Dispose();
        var defines = new List<string>();

        defines.Add($"VOXEL_BANKS {banksCount}");     // Обычно 32
        defines.Add($"CHUNKS_PER_BANK {chunksPerBank}"); // <--- ВАЖНО! Например, 65535

        if (GameSettings.UseProceduralWater) defines.Add("WATER_MODE_PROCEDURAL");
        if (GameSettings.EnableAO) defines.Add("ENABLE_AO");
        if (GameSettings.EnableWaterTransparency) defines.Add("ENABLE_WATER_TRANSPARENCY");
        if (GameSettings.BeamOptimization) defines.Add("ENABLE_BEAM_OPTIMIZATION");
        if (GameSettings.EnableLOD) defines.Add("ENABLE_LOD");

        switch (GameSettings.CurrentShadowMode)
        {
            case ShadowMode.Hard: defines.Add("SHADOW_MODE_HARD"); break;
            case ShadowMode.Soft: defines.Add("SHADOW_MODE_SOFT"); break;
        }

        Console.WriteLine($"[ShaderSystem] Compiling with defines: {string.Join(", ", defines)}");

        try
        {
            RaycastShader = new Shader("Shaders/raycast.vert", "Shaders/raycast.frag", defines);
            OnShaderReloaded?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ShaderSystem] CRITICAL ERROR: {ex.Message}");
        }
    }

    public void Use()
    {
        if (RaycastShader != null) RaycastShader.Use();
    }

    public void Dispose()
    {
        RaycastShader?.Dispose();
    }
}