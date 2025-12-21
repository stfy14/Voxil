// /Physics/VoxelPhysicsBuilder.cs
using OpenTK.Mathematics;
using System.Collections.Generic;
using BepuVector3 = System.Numerics.Vector3;
public struct VoxelCollider
{
public BepuVector3 Position;
public BepuVector3 HalfSize;
}
public static class VoxelPhysicsBuilder
{
public static List<VoxelCollider> GenerateColliders(MaterialType[] voxels, Vector3i chunkPos)
{
var colliders = new List<VoxelCollider>();
int size = Chunk.ChunkSize;
bool IsSolid(int x, int y, int z)
    {
        if (x < 0 || x >= size || y < 0 || y >= size || z < 0 || z >= size) return false;
        return MaterialRegistry.IsSolidForPhysics(voxels[x + size * (y + size * z)]);
    }

    bool[] visited = new bool[Chunk.Volume];

    for (int y = 0; y < size; y++)
    {
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int index = x + size * (y + size * z);
                
                if (!visited[index] && MaterialRegistry.IsSolidForPhysics(voxels[index]))
                {
                    // Оптимизация внутренних блоков
                    if (IsSolid(x + 1, y, z) && IsSolid(x - 1, y, z) &&
                        IsSolid(x, y + 1, z) && IsSolid(x, y - 1, z) &&
                        IsSolid(x, y, z + 1) && IsSolid(x, y, z - 1))
                    {
                        visited[index] = true;
                        continue;
                    }

                    // Greedy Strip по X
                    int width = 1;
                    while (x + width < size)
                    {
                        int nextIdx = (x + width) + size * (y + size * z);
                        if (visited[nextIdx] || !MaterialRegistry.IsSolidForPhysics(voxels[nextIdx])) break;
                        
                        // Прерываем полоску, если следующий блок внутренний? 
                        // Для простоты - нет, объединяем всё, что снаружи.
                        width++;
                    }

                    // Помечаем как посещенные
                    for(int k=0; k<width; k++) visited[(x+k) + size*(y+size*z)] = true;

                    float centerX = x + width / 2f;
                    float centerY = y + 0.5f;
                    float centerZ = z + 0.5f;

                    var collider = new VoxelCollider
                    {
                        Position = new BepuVector3(centerX, centerY, centerZ),
                        HalfSize = new BepuVector3(width / 2f, 0.5f, 0.5f)
                    };
                    colliders.Add(collider);
                    
                    x += width - 1;
                }
            }
        }
    }
    return colliders;
}
}