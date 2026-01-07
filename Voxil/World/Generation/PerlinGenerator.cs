using OpenTK.Mathematics;

public class PerlinGenerator : IWorldGenerator
{
    private readonly PerlinNoise _noise;
    // Настройки масштаба для микро-вокселей
    private const double NoiseScale = 0.03; 
    private const int BaseHeightMeters = 40; 
    private const int AmplitudeMeters = 20;  
    private const int SeaLevelMeters = 35;          

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
                
                // Высота ландшафта в этой точке (в метрах)
                double noiseVal = _noise.Noise(wx * NoiseScale, wz * NoiseScale); 
                int terrainHeightMeters = BaseHeightMeters + (int)(noiseVal * AmplitudeMeters);

                for (int y = 0; y < res; y++)
                {
                    float wy = worldBaseY + (y * step);
                    int index = x + res * (y + res * z);

                    if (wy < terrainHeightMeters)
                    {
                        // Верхний слой (1 метр) - земля, ниже - камень
                        if (wy < terrainHeightMeters - 1.0f) 
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