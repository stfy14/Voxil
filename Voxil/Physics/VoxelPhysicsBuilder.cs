using OpenTK.Mathematics;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
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
    private const int Size = Constants.ChunkResolution; 
    private const int Volume = Constants.ChunkVolume;   
    private const float VoxelSize = Constants.VoxelSize;

    // Быстрая таблица твердости материалов (чтобы не вызывать методы)
    private static readonly bool[] _solidLookup = new bool[256];

    static VoxelPhysicsBuilder()
    {
        // Инициализируем таблицу один раз при старте
        for (int i = 0; i < 256; i++)
        {
            _solidLookup[i] = MaterialRegistry.IsSolidForPhysics((MaterialType)i);
        }
    }

    // Теперь метод принимает буфер visited извне, чтобы не тратить время на аллокацию
    public static unsafe PhysicsBuildResultData GenerateColliders(
        MaterialType[] voxels, 
        Vector3i chunkPos, 
        bool[] reusedVisitedBuffer)
    {
        // Очищаем буфер посещенных вокселей (это очень быстро, почти как memset)
        Array.Clear(reusedVisitedBuffer, 0, Volume);

        // Арендуем массив для коллайдеров (тут без изменений)
        VoxelCollider[] collidersBuffer = ArrayPool<VoxelCollider>.Shared.Rent(Volume);
        int colliderCount = 0;

        // Фиксируем массивы в памяти, чтобы GC их не двигал, и мы могли использовать указатели
        fixed (MaterialType* pVoxels = voxels)
        fixed (bool* pVisited = reusedVisitedBuffer)
        fixed (bool* pSolidLookup = _solidLookup)
        {
            for (int z = 0; z < Size; z++)
            {
                // Предварительный расчет смещения по Z
                int zOffset = z * Size * Size;

                for (int y = 0; y < Size; y++)
                {
                    // Смещение по Y
                    int yOffset = zOffset + y * Size;

                    for (int x = 0; x < Size; x++)
                    {
                        int index = yOffset + x;

                        // 1. Быстрая проверка: посещен ли?
                        if (pVisited[index]) continue;

                        // 2. Быстрая проверка: твердый ли? (через lookup table)
                        // Приводим byte материала к int для индекса
                        byte matByte = (byte)pVoxels[index];
                        if (!pSolidLookup[matByte]) continue;

                        // --- НАЧАЛО GREEDY MESHING ---
                        
                        // Растем по X
                        int width = 1;
                        while (x + width < Size)
                        {
                            int nextIdx = index + width;
                            if (pVisited[nextIdx]) break;
                            
                            byte nextMat = (byte)pVoxels[nextIdx];
                            if (!pSolidLookup[nextMat]) break;

                            width++;
                        }

                        // Растем по Y
                        int height = 1;
                        while (y + height < Size)
                        {
                            int nextRowBase = zOffset + (y + height) * Size;
                            bool rowValid = true;
                            for (int k = 0; k < width; k++)
                            {
                                int checkIdx = nextRowBase + x + k;
                                byte checkMat = (byte)pVoxels[checkIdx];
                                
                                // Проверка: посещен или не твердый
                                if (pVisited[checkIdx] || !pSolidLookup[checkMat])
                                {
                                    rowValid = false;
                                    break;
                                }
                            }
                            if (!rowValid) break;
                            height++;
                        }

                        // Растем по Z
                        int depth = 1;
                        while (z + depth < Size)
                        {
                            int nextSliceBase = (z + depth) * Size * Size;
                            bool sliceValid = true;

                            // Проверяем плоскость width * height
                            for (int py = 0; py < height; py++)
                            {
                                int rowBase = nextSliceBase + (y + py) * Size;
                                for (int px = 0; px < width; px++)
                                {
                                    int checkIdx = rowBase + x + px;
                                    byte checkMat = (byte)pVoxels[checkIdx];

                                    if (pVisited[checkIdx] || !pSolidLookup[checkMat])
                                    {
                                        sliceValid = false;
                                        goto EndDepthCheck; // Быстрый выход из вложенных циклов
                                    }
                                }
                            }
                            EndDepthCheck:
                            if (!sliceValid) break;
                            depth++;
                        }

                        // --- ЗАПИСЬ РЕЗУЛЬТАТА ---

                        // Помечаем воксели как посещенные
                        // (Оптимизированная запись)
                        for (int d = 0; d < depth; d++)
                        {
                            int markZBase = (z + d) * Size * Size;
                            for (int h = 0; h < height; h++)
                            {
                                int markRowBase = markZBase + (y + h) * Size + x;
                                for (int w = 0; w < width; w++)
                                {
                                    pVisited[markRowBase + w] = true;
                                }
                            }
                        }

                        // Добавляем коллайдер
                        // (x, y, z) - это индексы начала
                        // Центр бокса = (Index + Size/2) * VoxelSize
                        
                        float centerX = (x + width * 0.5f) * VoxelSize;
                        float centerY = (y + height * 0.5f) * VoxelSize;
                        float centerZ = (z + depth * 0.5f) * VoxelSize;

                        collidersBuffer[colliderCount] = new VoxelCollider
                        {
                            Position = new BepuVector3(centerX, centerY, centerZ),
                            HalfSize = new BepuVector3(
                                width * VoxelSize * 0.5f, 
                                height * VoxelSize * 0.5f, 
                                depth * VoxelSize * 0.5f
                            )
                        };
                        colliderCount++;
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
}