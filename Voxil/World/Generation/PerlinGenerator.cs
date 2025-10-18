using OpenTK.Mathematics;
using System.Collections.Generic;

public class PerlinGenerator : IWorldGenerator
{
    private readonly PerlinNoise _noise;
    private const int WorldHeight = 64;
    private const int TerrainBaseHeight = 30;

    public PerlinGenerator(int seed)
    {
        _noise = new PerlinNoise(seed);
    }

    public void GenerateChunk(Chunk chunk, HashSet<Vector3i> voxels)
    {
        var chunkWorldPos = chunk.Position * Chunk.ChunkSize;

        for (int x = 0; x < Chunk.ChunkSize; x++)
        {
            for (int z = 0; z < Chunk.ChunkSize; z++)
            {
                // Получаем мировые координаты X и Z для точки шума
                int worldX = chunkWorldPos.X + x;
                int worldZ = chunkWorldPos.Z + z;

                // Используем шум для определения высоты ландшафта
                double height = _noise.Noise(worldX * 0.02, worldZ * 0.02); // 0.02 - масштаб
                int terrainHeight = TerrainBaseHeight + (int)(height * 15); // 15 - амплитуда

                for (int y = 0; y < terrainHeight; y++)
                {
                    if (y < WorldHeight)
                    {
                        voxels.Add(new Vector3i(x, y, z));
                    }
                }
            }
        }
    }
}