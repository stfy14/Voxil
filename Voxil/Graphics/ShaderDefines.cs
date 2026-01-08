using System;
using System.Globalization;

public static class ShaderDefines
{
    public static string GetGlslDefines()
    {
        int hardShadowDistMeters = 200;
        int hardSteps = (int)(hardShadowDistMeters / Constants.VoxelSize);
        if (hardSteps < 10) hardSteps = 10;

        var sb = new System.Text.StringBuilder();
    
        // Вставляем базовые константы
        sb.AppendLine($@"
       #define CHUNK_SIZE {Constants.ChunkSizeWorld}
       #define VOXEL_RESOLUTION {Constants.ChunkResolution}
       #define VOXELS_PER_METER {Constants.VoxelsPerMeter.ToString("F1", CultureInfo.InvariantCulture)}
       #define BIT_SHIFT {Constants.BitShift}
       #define BIT_MASK {Constants.BitMask}
       #define HARD_SHADOW_STEPS {hardSteps}
       #define SOFT_SHADOW_STEPS 32
    ");

        // Вставляем настройки пользователя
        if (GameSettings.EnableAO) 
            sb.AppendLine("#define ENABLE_AO");
        if (GameSettings.EnableWaterTransparency) 
            sb.AppendLine("#define ENABLE_WATER_TRANSPARENCY");
        if (GameSettings.BeamOptimization) 
            sb.AppendLine("#define ENABLE_BEAM_OPTIMIZATION");  

        return sb.ToString();
    }
}