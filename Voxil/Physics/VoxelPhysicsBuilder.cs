using OpenTK.Mathematics;
using System;
using System.Runtime.CompilerServices;
using BepuVector3 = System.Numerics.Vector3;

public struct VoxelCollider
{
    public BepuVector3 Position;
    public BepuVector3 HalfSize;
}

// Вернули имя PhysicsBuildResultData
public struct PhysicsBuildResultData
{
    public VoxelCollider[] CollidersArray;
    public int Count;
    
    // Dispose больше не нужен, память управляется GC
}

public static class VoxelPhysicsBuilder
{
    private const int Size = Constants.ChunkResolution; 
    private const float VoxelSize = Constants.VoxelSize;

    private static readonly bool[] _solidLookup = new bool[256];

    static VoxelPhysicsBuilder()
    {
        for (int i = 0; i < 256; i++) _solidLookup[i] = MaterialRegistry.IsSolidForPhysics((MaterialType)i);
    }

    public static unsafe int GenerateColliders(
        MaterialType[] voxels, 
        bool[] visitedBuffer, 
        VoxelCollider[] scratchBuffer)
    {
        Array.Clear(visitedBuffer, 0, visitedBuffer.Length);
        int colliderCount = 0;

        fixed (MaterialType* pVoxels = voxels)
        fixed (bool* pVisited = visitedBuffer)
        fixed (bool* pSolidLookup = _solidLookup)
        fixed (VoxelCollider* pOutput = scratchBuffer)
        {
            for (int z = 0; z < Size; z++)
            {
                int zOffset = z * Size * Size;
                for (int y = 0; y < Size; y++)
                {
                    int yOffset = zOffset + y * Size;
                    for (int x = 0; x < Size; x++)
                    {
                        int index = yOffset + x;

                        if (pVisited[index]) continue;
                        if (!pSolidLookup[(byte)pVoxels[index]]) continue;

                        int width = 1;
                        while (x + width < Size)
                        {
                            int nextIdx = index + width;
                            if (pVisited[nextIdx] || !pSolidLookup[(byte)pVoxels[nextIdx]]) break;
                            width++;
                        }

                        int height = 1;
                        while (y + height < Size)
                        {
                            int nextRowBase = zOffset + (y + height) * Size;
                            bool rowValid = true;
                            for (int k = 0; k < width; k++)
                            {
                                int idx2 = nextRowBase + x + k;
                                if (pVisited[idx2] || !pSolidLookup[(byte)pVoxels[idx2]]) { rowValid = false; break; }
                            }
                            if (!rowValid) break;
                            height++;
                        }

                        int depth = 1;
                        while (z + depth < Size)
                        {
                            int nextSliceBase = (z + depth) * Size * Size;
                            bool sliceValid = true;
                            for (int py = 0; py < height; py++)
                            {
                                int rowBase = nextSliceBase + (y + py) * Size;
                                for (int px = 0; px < width; px++)
                                {
                                    int idx3 = rowBase + x + px;
                                    if (pVisited[idx3] || !pSolidLookup[(byte)pVoxels[idx3]]) 
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

                        for (int d = 0; d < depth; d++)
                        {
                            int markZ = (z + d) * Size * Size;
                            for (int h = 0; h < height; h++)
                            {
                                int markRow = markZ + (y + h) * Size + x;
                                for (int w = 0; w < width; w++) pVisited[markRow + w] = true;
                            }
                        }

                        float cx = (x + width * 0.5f) * VoxelSize;
                        float cy = (y + height * 0.5f) * VoxelSize;
                        float cz = (z + depth * 0.5f) * VoxelSize;

                        pOutput[colliderCount] = new VoxelCollider
                        {
                            Position = new BepuVector3(cx, cy, cz),
                            HalfSize = new BepuVector3(width * VoxelSize * 0.5f, height * VoxelSize * 0.5f, depth * VoxelSize * 0.5f)
                        };
                        colliderCount++;
                    }
                }
            }
        }
        return colliderCount;
    }
}