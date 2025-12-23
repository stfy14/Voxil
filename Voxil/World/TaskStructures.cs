using OpenTK.Mathematics;

// Задача для генератора: какую позицию генерировать и с каким приоритетом
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

// Результат работы генератора: позиция и сырые данные вокселей
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

// Задача для физического строителя: какой чанк обрабатывать
public readonly struct PhysicsBuildTask
{
    public readonly Chunk ChunkToProcess;
    public bool IsValid => ChunkToProcess != null;

    public PhysicsBuildTask(Chunk chunk)
    {
        ChunkToProcess = chunk;
    }
}

// Результат построения физики: чанк и рассчитанные коллайдеры
public readonly struct PhysicsBuildResult
{
    public readonly Chunk TargetChunk;
    public readonly PhysicsBuildResultData Data; // Эта структура определена в VoxelPhysicsBuilder.cs
    public readonly bool IsValid;

    public PhysicsBuildResult(Chunk chunk, PhysicsBuildResultData data)
    {
        TargetChunk = chunk;
        Data = data;
        IsValid = chunk != null;
    }
}