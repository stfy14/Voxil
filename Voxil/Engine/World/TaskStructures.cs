using OpenTK.Mathematics;

public readonly struct ChunkGenerationTask
{
    public readonly Vector3i Position;
    public readonly int Priority;

    public ChunkGenerationTask(Vector3i pos, int priority)
    {
        Position = pos;
        Priority = priority;
    }
}

public readonly struct ChunkGenerationResult
{
    public readonly Vector3i Position;
    public readonly MaterialType[] Voxels;

    public ChunkGenerationResult(Vector3i pos, MaterialType[] voxels)
    {
        Position = pos;
        Voxels = voxels;
    }
}

public readonly struct PhysicsBuildTask
{
    public readonly Chunk ChunkToProcess;
    public bool IsValid => ChunkToProcess != null;
    public PhysicsBuildTask(Chunk chunk) { ChunkToProcess = chunk; }
}

public readonly struct PhysicsBuildResult
{
    public readonly Chunk TargetChunk;
    public readonly PhysicsBuildResultData Data;
    public readonly bool IsValid;

    public PhysicsBuildResult(Chunk chunk, PhysicsBuildResultData data) 
    { 
        TargetChunk = chunk; 
        Data = data; 
        IsValid = chunk != null; 
    }
}