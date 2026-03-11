// --- Engine/Graphics/Shader/ShaderSystem.cs ---
using System;
using System.Collections.Generic;

public class ShaderSystem : IDisposable
{
    public Shader RaycastShader         { get; private set; }
    public Shader ShadowShader          { get; private set; }
    public Shader ShadowUpsampleShader  { get; private set; }
    public Shader CompositeShader       { get; private set; }
    public Shader EditUpdaterShader     { get; private set; }
    public Shader GIProbeUpdateShader   { get; private set; } // ← НОВЫЙ

    public event Action OnShaderReloaded;

    public void Compile(int banksCount, int chunksPerBank)
    {
        RaycastShader?.Dispose();
        ShadowShader?.Dispose();
        ShadowUpsampleShader?.Dispose();
        CompositeShader?.Dispose();
        EditUpdaterShader?.Dispose();
        GIProbeUpdateShader?.Dispose();

        var defines = ShaderDefines.GetRuntimeDefines(banksCount, chunksPerBank);
        Shader.GlobalBanksInjection = ShaderDefines.GenerateBanksCode(banksCount);

        // 1. СНАЧАЛА добавляем ENABLE_GI в базовый список (если включено)
        if (GameSettings.EnableGI)
        {
            defines.Add("ENABLE_GI");
        }

        // 2. ТОЛЬКО ПОСЛЕ ЭТОГО клонируем список для шейдера теней!
        var shadowDefines = new List<string>(defines) { "SHADOW_PASS" };

        try
        {
            RaycastShader = new Shader(ShaderPaths.RaycastVert, ShaderPaths.RaycastFrag, defines);
            ShadowShader = new Shader(ShaderPaths.RaycastVert, ShaderPaths.ShadowFrag, shadowDefines); // Теперь он видит GI!
            ShadowUpsampleShader = new Shader(ShaderPaths.RaycastVert, ShaderPaths.ShadowUpsampleFrag, defines);
            CompositeShader = new Shader(ShaderPaths.RaycastVert, ShaderPaths.CompositeFrag, defines);
            EditUpdaterShader = new Shader(ShaderPaths.EditUpdater, defines);

            // Compute-шейдеру достаточно базового списка, так как там уже есть ENABLE_GI
            if (GameSettings.EnableGI)
            {
                GIProbeUpdateShader = new Shader(ShaderPaths.GIProbeUpdate, defines);
                Console.WriteLine("[ShaderSystem] GI probe update shader compiled.");
            }

            OnShaderReloaded?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ShaderSystem] CRITICAL ERROR: {ex.Message}");
        }
    }

    public void Use() => RaycastShader?.Use();

    public void Dispose()
    {
        RaycastShader?.Dispose();
        ShadowShader?.Dispose();
        ShadowUpsampleShader?.Dispose();
        CompositeShader?.Dispose();
        EditUpdaterShader?.Dispose();
        GIProbeUpdateShader?.Dispose();
    }
}