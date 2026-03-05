using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System.Collections.Generic;

// -------------------------------------------------------------------------
// Редактирование вокселей статического мира
// -------------------------------------------------------------------------
public interface IVoxelEditService
{
    // Чтение
    MaterialType GetMaterialGlobal(Vector3i globalPos);
    bool IsVoxelSolidGlobal(Vector3i globalPos);
    void GetStaticVoxelHealthInfo(Vector3i globalPos, out float currentHP, out float maxHP);

    // Изменение
    bool RemoveVoxelGlobal(Vector3i globalPos);
    bool RemoveVoxelGlobal(Vector3i globalPos, bool updateMesh);
    void ApplyDamageToStatic(Vector3i globalPos, float damage, out bool destroyed);
    void DestroyVoxelAt(CollidableReference collidable, System.Numerics.Vector3 hitPoint, System.Numerics.Vector3 hitNormal);

    // Грязные чанки (батчевое обновление после взрывов)
    void MarkChunkDirty(Vector3i globalVoxelIndex);
    void UpdateDirtyChunks();
}