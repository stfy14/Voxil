// /World/Generation/IWorldGenerator.cs
using OpenTK.Mathematics;
using System.Collections.Generic;

public interface IWorldGenerator
{
    // ИЗМЕНЕНО: теперь генератор заполняет данными переданный чанк
    void GenerateChunk(Chunk chunk, HashSet<Vector3i> voxels);
}