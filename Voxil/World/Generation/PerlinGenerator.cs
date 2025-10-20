using OpenTK.Mathematics;
using System.Collections.Generic;

public class PerlinGenerator : IWorldGenerator
{
    private readonly PerlinNoise _noise;

    // --- НАСТРОЙКИ СТАБИЛЬНОГО ГЕНЕРАТОРА ---
    private const double TerrainScale = 0.03; // Масштаб основного ландшафта
    private const int TerrainBaseHeight = 40; // Базовая высота земли
    private const int TerrainAmplitude = 20;  // Максимальная высота холмов над базой
    private const int SeaLevel = 35;          // Уровень воды

    public PerlinGenerator(int seed)
    {
        _noise = new PerlinNoise(seed);
    }

    public void GenerateChunk(Vector3i chunkPosition, Dictionary<Vector3i, MaterialType> voxels)
    {
        var chunkWorldPos = chunkPosition * Chunk.ChunkSize;

        // 1. Генерируем основной ландшафт из камня и земли
        for (int x = 0; x < Chunk.ChunkSize; x++)
        {
            for (int z = 0; z < Chunk.ChunkSize; z++)
            {
                double worldX = (chunkWorldPos.X + x) * TerrainScale;
                double worldZ = (chunkWorldPos.Z + z) * TerrainScale;

                double heightNoise = _noise.Noise(worldX, worldZ); // Шум от -1 до 1

                // Преобразуем шум в высоту ландшафта
                int terrainHeight = TerrainBaseHeight + (int)(heightNoise * TerrainAmplitude);

                for (int y = 0; y < terrainHeight; y++)
                {
                    MaterialType material;
                    if (y < terrainHeight - 4) // Нижние слои - камень
                        material = MaterialType.Stone;
                    else // Верхние 4 слоя - земля
                        material = MaterialType.Dirt;

                    voxels.Add(new Vector3i(x, y, z), material);
                }
            }
        }

        // 2. Добавляем воду
        for (int x = 0; x < Chunk.ChunkSize; x++)
        {
            for (int z = 0; z < Chunk.ChunkSize; z++)
            {
                for (int y = 0; y <= SeaLevel; y++)
                {
                    var pos = new Vector3i(x, y, z);
                    // Если в этой точке пусто (нет земли), то добавляем воду
                    if (!voxels.ContainsKey(pos))
                    {
                        voxels.Add(pos, MaterialType.Water);
                    }
                }
            }
        }
    }
}