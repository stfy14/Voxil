// /Physics/VoxelPhysicsBuilder.cs
using OpenTK.Mathematics;
using System;
using System.Buffers; // <--- Не забудь!
using System.Collections.Generic;
using BepuVector3 = System.Numerics.Vector3;

public struct VoxelCollider
{
    public BepuVector3 Position;
    public BepuVector3 HalfSize;
}

// Структура-результат, чтобы вернуть массив и его реальную длину
public struct PhysicsBuildResultData : IDisposable
{
    public VoxelCollider[] CollidersArray;
    public int Count;

    public void Dispose()
    {
        if (CollidersArray != null)
        {
            ArrayPool<VoxelCollider>.Shared.Return(CollidersArray);
            CollidersArray = null;
        }
    }
}

public static class VoxelPhysicsBuilder
{
    // Меняем возвращаемый тип с List на нашу структуру
    public static PhysicsBuildResultData GenerateColliders(MaterialType[] voxels, Vector3i chunkPos)
    {
        int size = Chunk.ChunkSize;
        int volume = Chunk.Volume;

        // 1. Арендуем массив visited (вместо new bool[])
        bool[] visited = ArrayPool<bool>.Shared.Rent(volume);
        Array.Clear(visited, 0, volume); // Обязательно чистим, так как он из пула

        // 2. Арендуем массив для коллайдеров (максимум коллайдеров = объему чанка, хоть это и редкость)
        // Это предотвращает ресайзы List<>, которые убивают память.
        VoxelCollider[] collidersBuffer = ArrayPool<VoxelCollider>.Shared.Rent(volume);
        int colliderCount = 0;

        try
        {
            bool IsSolid(int x, int y, int z)
            {
                if (x < 0 || x >= size || y < 0 || y >= size || z < 0 || z >= size) return false;
                // ВАЖНО: voxels тоже может быть больше объема (из-за пула), используем правильный индекс
                return MaterialRegistry.IsSolidForPhysics(voxels[x + size * (y + size * z)]);
            }

            for (int y = 0; y < size; y++)
            {
                for (int z = 0; z < size; z++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int index = x + size * (y + size * z);

                        if (!visited[index] && MaterialRegistry.IsSolidForPhysics(voxels[index]))
                        {
                            // Оптимизация внутренних блоков (без изменений)
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
                                width++;
                            }

                            for (int k = 0; k < width; k++) visited[(x + k) + size * (y + size * z)] = true;

                            // Записываем в массив вместо List.Add
                            collidersBuffer[colliderCount] = new VoxelCollider
                            {
                                Position = new BepuVector3(x + width / 2f, y + 0.5f, z + 0.5f),
                                HalfSize = new BepuVector3(width / 2f, 0.5f, 0.5f)
                            };
                            colliderCount++;
                            
                            x += width - 1;
                        }
                    }
                }
            }

            return new PhysicsBuildResultData 
            { 
                CollidersArray = collidersBuffer, 
                Count = colliderCount 
            };
        }
        finally
        {
            // Возвращаем visited сразу же
            ArrayPool<bool>.Shared.Return(visited);
        }
    }
}