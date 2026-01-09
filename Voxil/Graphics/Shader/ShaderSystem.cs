using System;
using System.Collections.Generic;

public class ShaderSystem : IDisposable
{
    public Shader RaycastShader { get; private set; }
    public event Action OnShaderReloaded;

    public void Compile(int voxelBanksCount)
    {
        RaycastShader?.Dispose();
        var defines = new List<string>();

        // 1. Системные
        defines.Add($"VOXEL_BANKS {voxelBanksCount}");

        // 2. Настройки графики
        if (GameSettings.UseProceduralWater) defines.Add("WATER_MODE_PROCEDURAL");
        if (GameSettings.EnableAO) defines.Add("ENABLE_AO");
        
        // --- ВАЖНЫЙ ФИКС: ДОБАВЛЕНА ПЕРЕДАЧА НАСТРОЙКИ ПРОЗРАЧНОСТИ ---
        if (GameSettings.EnableWaterTransparency) defines.Add("ENABLE_WATER_TRANSPARENCY");
        
        if (GameSettings.BeamOptimization) defines.Add("ENABLE_BEAM_OPTIMIZATION");

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