using System;
using System.Collections.Generic;

public class ShaderSystem : IDisposable
{
    public Shader RaycastShader { get; private set; }
    
    // Событие, чтобы уведомить рендерер о том, что шейдер изменился (например, поменялись ID uniform-ов)
    public event Action OnShaderReloaded;

    public void Compile(int voxelBanksCount)
    {
        // Удаляем старый, если был
        RaycastShader?.Dispose();

        var defines = new List<string>();

        // 1. Критически важные системные настройки
        defines.Add($"VOXEL_BANKS {voxelBanksCount}");

        // 2. Графические настройки
        if (GameSettings.UseProceduralWater) defines.Add("WATER_MODE_PROCEDURAL");
        if (GameSettings.EnableAO) defines.Add("ENABLE_AO");
        if (GameSettings.EnableWaterTransparency) defines.Add("ENABLE_WATER_TRANSPARENCY");
        if (GameSettings.BeamOptimization) defines.Add("ENABLE_BEAM_OPTIMIZATION");

        switch (GameSettings.CurrentShadowMode)
        {
            case ShadowMode.Hard: defines.Add("SHADOW_MODE_HARD"); break;
            case ShadowMode.Soft: defines.Add("SHADOW_MODE_SOFT"); break;
        }

        Console.WriteLine($"[ShaderSystem] Compiling with {voxelBanksCount} banks...");

        try
        {
            RaycastShader = new Shader("Shaders/raycast.vert", "Shaders/raycast.frag", defines);
            OnShaderReloaded?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ShaderSystem] CRITICAL ERROR: {ex.Message}");
            // Здесь можно загрузить "розовый" фоллбэк-шейдер, чтобы игра не крашилась
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