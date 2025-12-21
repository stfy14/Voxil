// World/Generation/PerlinGenerator.cs
using OpenTK.Mathematics;

public class PerlinGenerator : IWorldGenerator
{
    private readonly PerlinNoise _noise;
    // Настройки ландшафта
    private const double Scale = 0.03; 
    private const int BaseHeight = 40; 
    private const int Amplitude = 20;  
    private const int SeaLevel = 35;          

    public PerlinGenerator(int seed)
    {
        _noise = new PerlinNoise(seed);
    }

    public void GenerateChunk(Vector3i chunkPosition, MaterialType[] voxels)
    {
        // Очищаем массив перед заполнением (хотя он обычно новый, но для надежности)
        System.Array.Fill(voxels, MaterialType.Air);

        // Мировые координаты начала чанка
        int worldXBase = chunkPosition.X * Chunk.ChunkSize;
        int worldYBase = chunkPosition.Y * Chunk.ChunkSize;
        int worldZBase = chunkPosition.Z * Chunk.ChunkSize;

        // Размер чанка (кэшируем для скорости)
        int size = Chunk.ChunkSize;

        for (int x = 0; x < size; x++)
        {
            int wx = worldXBase + x;
            
            for (int z = 0; z < size; z++)
            {
                int wz = worldZBase + z;

                // 1. Считаем высоту ландшафта (2D шум)
                // Оптимизация: вычисляем шум 1 раз на столбец, а не для каждого вокселя
                double noiseVal = _noise.Noise(wx * Scale, wz * Scale); 
                int terrainHeight = BaseHeight + (int)(noiseVal * Amplitude);

                // 2. Заполняем столбец вертикально
                for (int y = 0; y < size; y++)
                {
                    int wy = worldYBase + y;
                    
                    // Индекс в одномерном массиве: x + size * (y + size * z)
                    // Порядок должен совпадать с тем, как читает GpuRenderer и PhysicsBuilder!
                    // В GpuRenderer мы использовали: x + 16 * (y + 16 * z)
                    int index = x + size * (y + size * z);

                    if (wy < terrainHeight)
                    {
                        if (wy < terrainHeight - 4) 
                            voxels[index] = MaterialType.Stone;
                        else 
                            voxels[index] = MaterialType.Dirt;
                    }
                    else if (wy <= SeaLevel)
                    {
                        voxels[index] = MaterialType.Water;
                    }
                }
            }
        }
    }
}