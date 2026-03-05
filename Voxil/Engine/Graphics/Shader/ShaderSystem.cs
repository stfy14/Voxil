using System;

public class ShaderSystem : IDisposable
{
    public Shader RaycastShader { get; private set; }
    public Shader EditUpdaterShader { get; private set; }
    public event Action OnShaderReloaded;

    public void Compile(int banksCount, int chunksPerBank)
    {
        RaycastShader?.Dispose();
        EditUpdaterShader?.Dispose();

        // Получаем дефайны и код банков из ShaderDefines
        var defines = ShaderDefines.GetRuntimeDefines(banksCount, chunksPerBank);
        Shader.GlobalBanksInjection = ShaderDefines.GenerateBanksCode(banksCount);

        try
        {
            RaycastShader    = new Shader(ShaderPaths.RaycastVert, ShaderPaths.RaycastFrag, defines);
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
        EditUpdaterShader?.Dispose();
    }
}