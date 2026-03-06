using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public static class ShaderDefines
{
    // -------------------------------------------------------------------------
    // Константы движка — инжектируются в исходник шейдера как #define
    // Вычисляются один раз, не зависят от настроек рантайма
    // -------------------------------------------------------------------------
    public static string GetGlslDefines()
    {
        int hardShadowDistMeters = 200;
        int hardSteps = (int)(hardShadowDistMeters / Constants.VoxelSize);
        if (hardSteps < 10) hardSteps = 10;

        return $@"
            #define CHUNK_SIZE {Constants.ChunkSizeWorld}
            #define VOXEL_RESOLUTION {Constants.ChunkResolution}
            #define VOXELS_PER_METER {Constants.VoxelsPerMeter.ToString("F1", CultureInfo.InvariantCulture)}
            #define BIT_SHIFT {Constants.BitShift}
            #define BIT_MASK {Constants.BitMask}
            #define HARD_SHADOW_STEPS {hardSteps}
        ";
    }

    // -------------------------------------------------------------------------
    // Настройки рантайма — пересобираются при каждой перекомпиляции шейдеров
    // -------------------------------------------------------------------------
    public static List<string> GetRuntimeDefines(int banksCount, int chunksPerBank)
    {
        var defines = new List<string>
        {
            $"VOXEL_BANKS {banksCount}",
            $"CHUNKS_PER_BANK {chunksPerBank}"
        };

        if (GameSettings.IsEditorMode)            defines.Add("EDITOR_MODE");
        if (GameSettings.UseProceduralWater)      defines.Add("WATER_MODE_PROCEDURAL");
        if (GameSettings.EnableAO)                defines.Add("ENABLE_AO");
        if (GameSettings.EnableWaterTransparency) defines.Add("ENABLE_WATER_TRANSPARENCY");
        if (GameSettings.BeamOptimization)        defines.Add("ENABLE_BEAM_OPTIMIZATION");
        if (GameSettings.EnableLOD)               defines.Add("ENABLE_LOD");

        switch (GameSettings.CurrentShadowMode)
        {
            case ShadowMode.Hard: defines.Add("SHADOW_MODE_HARD"); break;
            case ShadowMode.Soft: defines.Add("SHADOW_MODE_SOFT"); break;
        }

        return defines;
    }

    // -------------------------------------------------------------------------
    // Генерация GLSL-кода для банков памяти
    // Инжектируется через /*__BANKS_INJECTION__*/ в шейдерах
    // -------------------------------------------------------------------------
    public static string GenerateBanksCode(int banksCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// Generated {banksCount} banks (Binding 8+)");

        // Объявление буферов
        for (int i = 0; i < banksCount; i++)
            sb.AppendLine($"layout(std430, binding = {8 + i}) buffer VoxelSSBO{i} {{ uint b{i}[]; }};");

        // Функция чтения (для tracing.glsl)
        sb.AppendLine("uint GetVoxelFromBank(uint bank, uint offset) {");
        sb.AppendLine("    switch(bank) {");
        for (int i = 0; i < banksCount; i++)
            sb.AppendLine($"        case {i}u: return b{i}[offset];");
        sb.AppendLine("    }");
        sb.AppendLine("    return 0u;");
        sb.AppendLine("}");

        // Функция записи (для edit_updater.comp)
        sb.AppendLine("void WriteVoxelToBank(uint bank, uint offset, uint mask, uint data) {");
        sb.AppendLine("    switch(bank) {");
        for (int i = 0; i < banksCount; i++)
            sb.AppendLine($"        case {i}u: atomicAnd(b{i}[offset], mask); atomicOr(b{i}[offset], data); break;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}