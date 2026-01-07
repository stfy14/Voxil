using System;
using System.Globalization;

public static class ShaderDefines
{
    public static string GetGlslDefines()
    {
        // Рассчитываем шаги теней в зависимости от размера вокселя.
        // Мы хотим, чтобы тень отбрасывалась на 200 метров реального мира.
        // Steps = DistanceMeters / VoxelSize
        
        int hardShadowDistMeters = 200;
        int softShadowDistMeters = 50;

        int hardSteps = (int)(hardShadowDistMeters / Constants.VoxelSize);
        int softSteps = (int)(softShadowDistMeters / Constants.VoxelSize);

        // Ограничиваем минимум, чтобы не сломалось
        if (hardSteps < 10) hardSteps = 10;
        if (softSteps < 5) softSteps = 5;

        return $@"
               // --- AUTOMATICALLY GENERATED DEFINES FROM C# ---
               #define CHUNK_SIZE {Constants.ChunkSizeWorld}
               #define VOXEL_RESOLUTION {Constants.ChunkResolution}
               #define VOXELS_PER_METER {Constants.VoxelsPerMeter.ToString("F1", CultureInfo.InvariantCulture)}
               #define BIT_SHIFT {Constants.BitShift}
               #define BIT_MASK {Constants.BitMask}

               // Shadow Steps (Dynamic based on Voxel Size)
               #define HARD_SHADOW_STEPS {hardSteps}
               #define SOFT_SHADOW_STEPS {softSteps}
               // -----------------------------------------------
               ";
    }
}