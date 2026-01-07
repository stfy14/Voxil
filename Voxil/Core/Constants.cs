public static class Constants
{
    // Размер чанка в МИРЕ (в метрах). Оставляем как было.
    public const int ChunkSizeWorld = 16; 

    // Размер одного вокселя (в метрах).
    // 0.25 = 25 см.
    public const float VoxelSize = 0.25f;

    // Разрешение чанка (сколько вокселей в одной стороне).
    // 16 / 0.25 = 64.
    public const int ChunkResolution = (int)(ChunkSizeWorld / VoxelSize);

    // Объем чанка в вокселях.
    // 64 * 64 * 64 = 262,144.
    public const int ChunkVolume = ChunkResolution * ChunkResolution * ChunkResolution;
}