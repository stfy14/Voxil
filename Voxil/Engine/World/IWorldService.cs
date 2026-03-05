using BepuPhysics;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

public interface IWorldService
{
    // ---- События ----
    event Action<Chunk> OnChunkLoaded;
    event Action<Chunk> OnChunkModified;
    event Action<Vector3i> OnChunkUnloaded;
    event Action<Vector3i> OnVoxelFastDestroyed;
    event Action<Chunk, Vector3i, MaterialType> OnVoxelEdited;

    // ---- Физика ----
    PhysicsWorld PhysicsWorld { get; }
    void RebuildPhysics(Chunk chunk);

    // ---- Чанки ----
    int LoadedChunkCount { get; }
    int GeneratorPendingCount { get; }
    Chunk GetChunk(Vector3i position);
    List<Chunk> GetChunksSnapshot();
    Dictionary<Vector3i, Chunk> GetAllChunks();
    bool IsChunkLoadedAt(Vector3i globalPos);
    float GetViewRangeInMeters();

    // ---- Уведомления (вызываются из сервисов) ----
    void NotifyVoxelEdited(Chunk chunk, Vector3i pos, MaterialType mat);
    void NotifyVoxelFastDestroyed(Vector3i worldPos);
    void NotifyChunkModified(Chunk chunk);

    // ---- Утилиты ----
    OpenTK.Mathematics.Vector3 GetPlayerPosition();
    System.Numerics.Vector3 GetPlayerVelocity();
    Vector3i GetChunkPosFromVoxelIndex(Vector3i voxelIndex);
    void SetGenerationThreadCount(int count);
    void ReloadWorld();
}