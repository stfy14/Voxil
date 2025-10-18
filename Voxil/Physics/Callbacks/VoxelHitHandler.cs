// /Physics/Callbacks/VoxelHitHandler.cs (новое имя файла)
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using System.Numerics;
using System.Runtime.CompilerServices;

public struct VoxelHitHandler : IRayHitHandler
{
    public bool Hit;
    public float T;
    public Vector3 Normal;
    public CollidableReference Collidable; // Храним общую ссылку, а не только BodyHandle

    public BodyHandle PlayerBodyHandle;
    public Simulation Simulation; // Нужна ссылка на симуляцию для проверки

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable)
    {
        // Не проверяем столкновение с игроком
        if (collidable.Mobility == CollidableMobility.Dynamic && collidable.BodyHandle == PlayerBodyHandle)
        {
            return false;
        }
        // Пропускаем "спящие" тела, если не хотим их будить
        return collidable.Mobility == CollidableMobility.Static || Simulation.Bodies.GetBodyReference(collidable.BodyHandle).Awake;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable, int childIndex)
    {
        return true; // Проверки на уровне дочерних объектов оставляем
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
    {
        if (t < maximumT)
        {
            maximumT = t;
            T = t;
            Normal = normal;
            Hit = true;
            Collidable = collidable;
        }
    }
}