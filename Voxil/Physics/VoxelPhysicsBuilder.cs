using OpenTK.Mathematics;
using System;
using System.Buffers;
using System.Collections.Generic;
using BepuVector3 = System.Numerics.Vector3;

public struct VoxelCollider
{
    public BepuVector3 Position;
    public BepuVector3 HalfSize;
}

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
    private const int Size = Constants.ChunkResolution; // 64
    private const int Volume = Constants.ChunkVolume;   // 262144
    private const float VoxelSize = Constants.VoxelSize; // 0.25f

    public static PhysicsBuildResultData GenerateColliders(MaterialType[] voxels, Vector3i chunkPos)
    {
        // 1. Арендуем массив visited, чтобы не выделять память в куче
        bool[] visited = ArrayPool<bool>.Shared.Rent(Volume);
        Array.Clear(visited, 0, Volume);

        // 2. Арендуем массив результатов (максимум может быть равен объему, но greedy сократит это в сотни раз)
        VoxelCollider[] collidersBuffer = ArrayPool<VoxelCollider>.Shared.Rent(Volume);
        int colliderCount = 0;

        try
        {
            // Хелпер для проверки твердости и посещенности
            // Индекс в массиве: x + Size * (y + Size * z)
            bool IsSolidAndUnvisited(int x, int y, int z)
            {
                if (x >= Size || y >= Size || z >= Size) return false;
                int idx = x + Size * (y + Size * z);
                return !visited[idx] && MaterialRegistry.IsSolidForPhysics(voxels[idx]);
            }

            // Хелпер для пометки
            void MarkVisited(int startX, int startY, int startZ, int sizeX, int sizeY, int sizeZ)
            {
                for (int z = startZ; z < startZ + sizeZ; z++)
                {
                    for (int y = startY; y < startY + sizeY; y++)
                    {
                        int rowStart = startX + Size * (y + Size * z);
                        // Заполняем линию по X (можно оптимизировать через Span.Fill, но так надежнее)
                        for (int k = 0; k < sizeX; k++)
                        {
                            visited[rowStart + k] = true;
                        }
                    }
                }
            }

            // --- GREEDY MESHING 3D ---
            // Проходим по всем вокселям
            for (int z = 0; z < Size; z++)
            {
                for (int y = 0; y < Size; y++)
                {
                    for (int x = 0; x < Size; x++)
                    {
                        // Если воксель пустой или уже обработан - пропускаем
                        if (!IsSolidAndUnvisited(x, y, z)) continue;

                        // 1. Растем по X (Ширина)
                        int width = 1;
                        while (IsSolidAndUnvisited(x + width, y, z))
                        {
                            width++;
                        }

                        // 2. Растем по Y (Высота) - проверяем, можно ли поднять весь ряд шириной 'width'
                        int height = 1;
                        bool canExtendY = true;
                        while (canExtendY)
                        {
                            // Проверяем следующий ряд по Y
                            int nextY = y + height;
                            if (nextY >= Size) break;

                            for (int k = 0; k < width; k++)
                            {
                                if (!IsSolidAndUnvisited(x + k, nextY, z))
                                {
                                    canExtendY = false;
                                    break;
                                }
                            }
                            if (canExtendY) height++;
                        }

                        // 3. Растем по Z (Глубина) - проверяем, можно ли углубить прямоугольник 'width * height'
                        int depth = 1;
                        bool canExtendZ = true;
                        while (canExtendZ)
                        {
                            int nextZ = z + depth;
                            if (nextZ >= Size) break;

                            for (int py = 0; py < height; py++)
                            {
                                for (int px = 0; px < width; px++)
                                {
                                    if (!IsSolidAndUnvisited(x + px, y + py, nextZ))
                                    {
                                        canExtendZ = false;
                                        break; // Выход из внутреннего цикла
                                    }
                                }
                                if (!canExtendZ) break; // Выход из среднего цикла
                            }
                            if (canExtendZ) depth++;
                        }

                        // 4. Добавляем коллайдер
                        // Центр коллайдера в локальных координатах чанка (метры)
                        // x,y,z - начало (в индексах)
                        // width, height, depth - размеры (в индексах)
                        // Координата центра = (Start + Size/2) * VoxelSize
                        
                        float centerX = (x + width / 2f) * VoxelSize;
                        float centerY = (y + height / 2f) * VoxelSize;
                        float centerZ = (z + depth / 2f) * VoxelSize;

                        collidersBuffer[colliderCount] = new VoxelCollider
                        {
                            Position = new BepuVector3(centerX, centerY, centerZ),
                            HalfSize = new BepuVector3(
                                (width * VoxelSize) / 2f, 
                                (height * VoxelSize) / 2f, 
                                (depth * VoxelSize) / 2f
                            )
                        };
                        colliderCount++;

                        // 5. Помечаем воксели как обработанные
                        MarkVisited(x, y, z, width, height, depth);
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
            ArrayPool<bool>.Shared.Return(visited);
        }
    }
}