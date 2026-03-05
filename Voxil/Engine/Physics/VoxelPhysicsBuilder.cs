// --- START OF FILE VoxelPhysicsBuilder.cs ---
using OpenTK.Mathematics;
using System;
using System.Runtime.CompilerServices;
using BepuVector3 = System.Numerics.Vector3;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

public struct VoxelCollider
{
    public BepuVector3 Position;
    public BepuVector3 HalfSize;
}

public struct PhysicsBuildResultData
{
    public VoxelCollider[] CollidersArray;
    public int Count;
}

public static class VoxelPhysicsBuilder
{
    private static readonly bool[] _solidLookup = new bool[256];

    static VoxelPhysicsBuilder()
    {
        for (int i = 0; i < 256; i++) _solidLookup[i] = MaterialRegistry.IsSolidForPhysics((MaterialType)i);
    }

    // Старый метод для Чанков (оставляем для совместимости, вызывает универсальный)
    public static int GenerateColliders(MaterialType[] voxels, bool[] visitedBuffer, VoxelCollider[] scratchBuffer)
    {
        return GenerateColliders(voxels, visitedBuffer, scratchBuffer, Constants.ChunkResolution, Constants.ChunkResolution, Constants.ChunkResolution);
    }

    // НОВЫЙ универсальный метод (для чанков и объектов)
   public static int GenerateColliders(
        MaterialType[] voxels, 
        bool[] visitedBuffer, 
        VoxelCollider[] scratchBuffer,
        int sizeX, int sizeY, int sizeZ,
        float scale = 1.0f)
    {
        Array.Clear(visitedBuffer, 0, visitedBuffer.Length);
        int colliderCount = 0;
        float voxelSize = Constants.VoxelSize * scale;

        // Получаем ссылки на начало массивов. 
        // Это безопасно, так как CLR знает, что мы работаем с этими массивами.
        ref MaterialType pVoxels = ref MemoryMarshal.GetArrayDataReference(voxels);
        ref bool pVisited = ref MemoryMarshal.GetArrayDataReference(visitedBuffer);
        ref bool pSolidLookup = ref MemoryMarshal.GetArrayDataReference(_solidLookup);
        ref VoxelCollider pOutput = ref MemoryMarshal.GetArrayDataReference(scratchBuffer);

        for (int z = 0; z < sizeZ; z++)
        {
            int zOffset = z * sizeX * sizeY;
            for (int y = 0; y < sizeY; y++)
            {
                int yOffset = zOffset + y * sizeX;
                for (int x = 0; x < sizeX; x++)
                {
                    int index = yOffset + x;

                    // Unsafe.Add заменяет индексацию с проверкой границ
                    if (Unsafe.Add(ref pVisited, index)) continue;
                    if (index >= voxels.Length) continue; 
                    
                    MaterialType currentVoxel = Unsafe.Add(ref pVoxels, index);
                    if (!Unsafe.Add(ref pSolidLookup, (byte)currentVoxel)) continue;

                    // --- GREEDY MESHING X ---
                    int width = 1;
                    while (x + width < sizeX)
                    {
                        int nextIdx = index + width;
                        if (Unsafe.Add(ref pVisited, nextIdx) || !Unsafe.Add(ref pSolidLookup, (byte)Unsafe.Add(ref pVoxels, nextIdx))) 
                            break;
                        width++;
                    }

                    // --- GREEDY MESHING Y ---
                    int height = 1;
                    while (y + height < sizeY)
                    {
                        int nextRowBase = zOffset + (y + height) * sizeX;
                        bool rowValid = true;
                        for (int k = 0; k < width; k++)
                        {
                            int idx2 = nextRowBase + x + k;
                            if (Unsafe.Add(ref pVisited, idx2) || !Unsafe.Add(ref pSolidLookup, (byte)Unsafe.Add(ref pVoxels, idx2))) 
                            { 
                                rowValid = false; 
                                break; 
                            }
                        }
                        if (!rowValid) break;
                        height++;
                    }

                    // --- GREEDY MESHING Z ---
                    int depth = 1;
                    while (z + depth < sizeZ)
                    {
                        int nextSliceBase = (z + depth) * sizeX * sizeY;
                        bool sliceValid = true;
                        for (int py = 0; py < height; py++)
                        {
                            int rowBase = nextSliceBase + (y + py) * sizeX;
                            for (int px = 0; px < width; px++)
                            {
                                int idx3 = rowBase + x + px;
                                if (Unsafe.Add(ref pVisited, idx3) || !Unsafe.Add(ref pSolidLookup, (byte)Unsafe.Add(ref pVoxels, idx3))) 
                                { 
                                    sliceValid = false; 
                                    goto EndDepthCheck; 
                                }
                            }
                        }
                        EndDepthCheck:
                        if (!sliceValid) break;
                        depth++;
                    }

                    // Помечаем как посещенные
                    for (int d = 0; d < depth; d++)
                    {
                        int markZ = (z + d) * sizeX * sizeY;
                        for (int h = 0; h < height; h++)
                        {
                            int markRow = markZ + (y + h) * sizeX + x;
                            for (int w = 0; w < width; w++) 
                                Unsafe.Add(ref pVisited, markRow + w) = true;
                        }
                    }

                    // Рассчитываем центр и размер
                    float cx = (x + width * 0.5f) * voxelSize;
                    float cy = (y + height * 0.5f) * voxelSize;
                    float cz = (z + depth * 0.5f) * voxelSize;

                    if (colliderCount < scratchBuffer.Length)
                    {
                        Unsafe.Add(ref pOutput, colliderCount) = new VoxelCollider
                        {
                            Position = new BepuVector3(cx, cy, cz),
                            HalfSize = new BepuVector3(width * voxelSize * 0.5f, height * voxelSize * 0.5f, depth * voxelSize * 0.5f)
                        };
                        colliderCount++;
                    }
                }
            }
        }
        return colliderCount;
    }
}