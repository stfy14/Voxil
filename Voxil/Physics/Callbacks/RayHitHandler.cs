// /Physics/Callbacks/RayHitHandler.cs
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using System.Numerics;
using System.Runtime.CompilerServices;

public struct RayHitHandler : IRayHitHandler
{
    public bool Hit;
    public float T;
    public BodyHandle Body;
    public Vector3 Normal;
    public CollidableReference Collidable;
    public BodyHandle BodyToIgnore;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable)
    {
        // Игнорируем только конкретное динамическое тело (например, игрока)
        if (collidable.Mobility == CollidableMobility.Dynamic &&
            collidable.BodyHandle.Value == BodyToIgnore.Value)
        {
            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable, int childIndex)
    {
        if (collidable.Mobility == CollidableMobility.Dynamic &&
            collidable.BodyHandle.Value == BodyToIgnore.Value)
        {
            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
    {
        if (t < maximumT)
        {
            maximumT = t;
            Hit = true;
            T = t;
            Normal = normal;
            Collidable = collidable;

            // Body заполняем только для динамических объектов
            if (collidable.Mobility == CollidableMobility.Dynamic)
            {
                Body = collidable.BodyHandle;
            }
            else
            {
                Body = default;
            }
        }
    }
}