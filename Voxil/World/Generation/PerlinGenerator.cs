using OpenTK.Mathematics;

public class PerlinGenerator : IWorldGenerator
{
    private readonly PerlinNoise _noise;
    
    // Настройки масштаба
    // NoiseScale влияет на "ширину" холмов. Чем меньше - тем более пологие холмы.
    private const double NoiseScale = 0.03; 
    
    private const float BaseHeightMeters = 40.0f; 
    private const float AmplitudeMeters = 20.0f;  
    private const float SeaLevelMeters = 35.0f;          

    public PerlinGenerator(int seed)
    {
        _noise = new PerlinNoise(seed);
    }

    public void GenerateChunk(Vector3i chunkPosition, MaterialType[] voxels)
    {
        // Очистка массива перед записью
        System.Array.Fill(voxels, MaterialType.Air);

        int res = Constants.ChunkResolution; // 64
        float step = Constants.VoxelSize;    // 0.25

        // Координаты чанка в метрах
        float worldBaseX = chunkPosition.X * Constants.ChunkSizeWorld;
        float worldBaseY = chunkPosition.Y * Constants.ChunkSizeWorld;
        float worldBaseZ = chunkPosition.Z * Constants.ChunkSizeWorld;

        for (int x = 0; x < res; x++)
        {
            float wx = worldBaseX + (x * step);
            for (int z = 0; z < res; z++)
            {
                float wz = worldBaseZ + (z * step);
                
                // ВАЖНО: Считаем высоту как float, НЕ округляя до int!
                // Иначе получим ступеньки высотой в 1 метр.
                double noiseVal = _noise.Noise(wx * NoiseScale, wz * NoiseScale); 
                float terrainHeight = BaseHeightMeters + (float)(noiseVal * AmplitudeMeters);

                for (int y = 0; y < res; y++)
                {
                    // Высота текущего вокселя
                    float wy = worldBaseY + (y * step);
                    int index = x + res * (y + res * z);

                    // Сравниваем float с float -> получаем точность 0.25м
                    if (wy < terrainHeight)
                    {
                        // Верхний слой (1 метр = 4 вокселя) - земля, ниже - камень
                        if (wy < terrainHeight - 1.0f) 
                            voxels[index] = MaterialType.Stone;
                        else 
                            voxels[index] = MaterialType.Dirt;
                    }
                    else if (wy <= SeaLevelMeters)
                    {
                        voxels[index] = MaterialType.Water;
                    }
                }
            }
        }
    }
}