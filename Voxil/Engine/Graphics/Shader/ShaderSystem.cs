using System;
using System.Collections.Generic;

public class ShaderSystem : IDisposable
{
    public Shader RaycastShader { get; private set; }
    public Shader ShadowShader { get; private set; }
    public Shader ShadowUpsampleShader { get; private set; }
    public Shader CompositeShader { get; private set; }
    public Shader EditUpdaterShader { get; private set; }
    public event Action OnShaderReloaded;

    public void Compile(int banksCount, int chunksPerBank)
    {
        RaycastShader?.Dispose();
        ShadowShader?.Dispose();
        ShadowUpsampleShader?.Dispose();
        CompositeShader?.Dispose();
        EditUpdaterShader?.Dispose();

        var defines = ShaderDefines.GetRuntimeDefines(banksCount, chunksPerBank);
        Shader.GlobalBanksInjection = ShaderDefines.GenerateBanksCode(banksCount);

        // shadow.frag компилируется с дополнительным define SHADOW_PASS
        var shadowDefines = new List<string>(defines) { "SHADOW_PASS" };

        try
        {
            RaycastShader = new Shader(ShaderPaths.RaycastVert, ShaderPaths.RaycastFrag, defines);
            ShadowShader = new Shader(ShaderPaths.RaycastVert, ShaderPaths.ShadowFrag, shadowDefines);
            ShadowUpsampleShader = new Shader(ShaderPaths.RaycastVert, ShaderPaths.ShadowUpsampleFrag, defines);
            CompositeShader = new Shader(ShaderPaths.RaycastVert, ShaderPaths.CompositeFrag, defines);
            EditUpdaterShader = new Shader(ShaderPaths.EditUpdater, defines);
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
    }
}