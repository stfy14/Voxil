// --- START OF FILE Constants.cs ---
using System;

public static class Constants
{
    // ==========================================
    // НАСТРОЙКИ (МЕНЯТЬ ТОЛЬКО ЗДЕСЬ)
    // ==========================================
    
    // Размер чанка в метрах
    public const int ChunkSizeWorld = 16; 

    // Размер одного вокселя в метрах
    // 0.5f   = 32^3
    // 0.25f  = 64^3
    // 0.125f = 128^3
    public const float VoxelSize = 0.25f; 

    // ==========================================
    // АВТОМАТИЧЕСКИЕ ВЫЧИСЛЕНИЯ (НЕ ТРОГАТЬ)
    // ==========================================ц

    // Разрешение чанка (сколько вокселей на сторону)
    public const int ChunkResolution = (int)(ChunkSizeWorld / VoxelSize);

    // Объем чанка
    public const int ChunkVolume = ChunkResolution * ChunkResolution * ChunkResolution;

    // Вокселей на метр (для шейдера)
    public const float VoxelsPerMeter = 1.0f / VoxelSize;

    // Сдвиг битов и маска для быстрой математики (эквивалент деления и остатка)
    // Math.Log2 возвращает double, приводим к int.
    // Например: для 128 это будет 7, для 64 это 6.
    public static readonly int BitShift = (int)Math.Log2(ChunkResolution);
    
    // Маска: для 128 это 127 (1111111), для 64 это 63.
    public const int BitMask = ChunkResolution - 1;
}