// --- START OF FILE Constants.cs ---
using System;

public static class Constants
{
    // ==========================================
    // НАСТРОЙКИ
    // ==========================================
    
    // Размер вокселя
    public const float VoxelSize = 1f; 

    // ЖЕЛАЕМОЕ РАЗРЕШЕНИЕ ЧАНКА (вокеслей на сторону)
    // 32 - очень быстро
    // 64 - норма (для 0.125f)
    // 128 - тяжело для CPU (то, что у вас сейчас)
    private const int TargetResolution = 32; 

    // ==========================================
    // АВТОМАТИЧЕСКИЕ ВЫЧИСЛЕНИЯ
    // ==========================================
    
    // Размер чанка в метрах ТЕПЕРЬ ВЫЧИСЛЯЕТСЯ
    public const int ChunkSizeWorld = (int)(TargetResolution * VoxelSize); 

    // Разрешение чанка
    public const int ChunkResolution = (int)(ChunkSizeWorld / VoxelSize);

    // Объем чанка
    public const int ChunkVolume = ChunkResolution * ChunkResolution * ChunkResolution;

    public const float VoxelsPerMeter = 1.0f / VoxelSize;

    public static readonly int BitShift = (int)Math.Log2(ChunkResolution);
    public const int BitMask = ChunkResolution - 1;
}