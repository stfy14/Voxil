// World/Generation/IWorldGenerator.cs
public interface IWorldGenerator
{
    // Теперь принимаем массив, а не Dictionary
    void GenerateChunk(OpenTK.Mathematics.Vector3i chunkPosition, MaterialType[] voxels);
}