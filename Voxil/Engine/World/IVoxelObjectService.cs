using OpenTK.Mathematics;
using System.Collections.Generic;
using System.Diagnostics;

// -------------------------------------------------------------------------
// Управление динамическими воксельными объектами
// -------------------------------------------------------------------------
public interface IVoxelObjectService
{
    // Получение
    List<VoxelObject> GetAllVoxelObjects();

    // Спавн
    void SpawnDynamicObject(VoxelObject obj, System.Numerics.Vector3 position, System.Numerics.Vector3 velocity);
    void SpawnComplexObject(System.Numerics.Vector3 position, List<Vector3i> localVoxels, MaterialType material);
    void SpawnComplexObject(System.Numerics.Vector3 position, List<Vector3i> localVoxels, MaterialType material, Dictionary<Vector3i, uint> perVoxelMaterials);

    // Уничтожение и расколы
    void DestroyVoxelObject(VoxelObject obj);
    void CreateDetachedObject(List<Vector3i> globalCluster);
    void ProcessDynamicObjectSplits(VoxelObject vo);

    // Обновление (вызывается из WorldManager.Update)
    void Update(Stopwatch mainThreadStopwatch);

    // Тесты
    void TestBreakVoxel(VoxelObject vo, Vector3i localPos);
}