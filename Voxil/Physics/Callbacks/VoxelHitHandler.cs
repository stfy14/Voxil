// /Physics/VoxelHitHandler.cs
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Trees;
using System.Numerics;
using System.Runtime.CompilerServices;

/// <summary>
/// Обработчик пересечений луча с вокселями (для разрушения блоков)
/// </summary>
public struct VoxelHitHandler : IRayHitHandler
{
    public BodyHandle PlayerBodyHandle;
    public Simulation Simulation;

    public bool Hit;
    public float T;
    public Vector3 Normal;
    public CollidableReference Collidable;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable)
    {
        // Игнорируем самого игрока
        if (collidable.Mobility == CollidableMobility.Dynamic)
        {
            return collidable.BodyHandle.Value != PlayerBodyHandle.Value;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable, int childIndex)
    {
        return AllowTest(collidable);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
    {
        // Сохраняем первое попадание (ближайшее)
        if (t < maximumT)
        {
            Hit = true;
            T = t;
            Normal = normal;
            Collidable = collidable;
            maximumT = t; // Обрезаем луч для последующих проверок
        }
    }
}