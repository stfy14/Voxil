// /World/Generation/IWorldGenerator.cs
using OpenTK.Mathematics;
using System.Collections.Generic;

public interface IWorldGenerator
{
    // ИЗМЕНЕНО: теперь генератор заполняет данными переданный чанк
    void GenerateChunk(Vector3i chunkPosition, Dictionary<Vector3i, MaterialType> voxels);
}