using System;
using System.Collections.Generic;
using System.Text;

public class ShaderSystem : IDisposable
{
    public Shader RaycastShader { get; private set; }
    public Shader EditUpdaterShader { get; private set; } // <--- Новый шейдер
    public event Action OnShaderReloaded;

    public void Compile(int banksCount, int chunksPerBank)
    {
        RaycastShader?.Dispose();
        EditUpdaterShader?.Dispose();

        var defines = new List<string>
        {
            $"VOXEL_BANKS {banksCount}",
            $"CHUNKS_PER_BANK {chunksPerBank}"
        };

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

        // === ГЕНЕРАЦИЯ КОДА ДЛЯ БАНКОВ ===
        // Биндинг начинается с 8, чтобы не пересекаться с 0..7 (служебные буферы)
        // Слоты: 8, 9, 10, 11, 12, 13, 14, 15 (итого 8 банков макс для лимита 16)
        var sb = new StringBuilder();
        sb.AppendLine($"// Generated {banksCount} banks (Binding 8+)");

        // 1. Объявление буферов
        for (int i = 0; i < banksCount; i++)
        {
            sb.AppendLine($"layout(std430, binding = {8 + i}) buffer VoxelSSBO{i} {{ uint b{i}[]; }};");
        }

        // 2. Функция чтения (для tracing.glsl)
        sb.AppendLine("uint GetVoxelFromBank(uint bank, uint offset) {");
        sb.AppendLine("    switch(bank) {");
        for (int i = 0; i < banksCount; i++)
            sb.AppendLine($"        case {i}u: return b{i}[offset];");
        sb.AppendLine("    }");
        sb.AppendLine("    return 0u;");
        sb.AppendLine("}");

        // 3. Функция записи (для edit_updater.comp)
        sb.AppendLine("void WriteVoxelToBank(uint bank, uint offset, uint mask, uint data) {");
        sb.AppendLine("    switch(bank) {");
        for (int i = 0; i < banksCount; i++)
            sb.AppendLine($"        case {i}u: atomicAnd(b{i}[offset], mask); atomicOr(b{i}[offset], data); break;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        // Передаем код в Shader класс
        Shader.GlobalBanksInjection = sb.ToString();
        // =================================

        try
        {
            RaycastShader = new Shader("Shaders/raycast.vert", "Shaders/raycast.frag", defines);
            EditUpdaterShader = new Shader("Shaders/edit_updater.comp", defines);
            OnShaderReloaded?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ShaderSystem] CRITICAL ERROR: {ex.Message}");
        }
    }

    public void Use() => RaycastShader?.Use();
    public void Dispose() { RaycastShader?.Dispose(); EditUpdaterShader?.Dispose(); }
}