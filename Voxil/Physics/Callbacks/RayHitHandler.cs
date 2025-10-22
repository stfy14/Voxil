// /Physics/RayHitHandler.cs
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Trees;
using System.Numerics;
using System.Runtime.CompilerServices;

/// <summary>
/// Общий обработчик пересечений луча (для проверки земли под игроком и т.д.)
/// </summary>
public struct RayHitHandler : IRayHitHandler
{
    public BodyHandle BodyToIgnore;

    public bool Hit;
    public float T;
    public Vector3 Normal;
    public BodyHandle Body;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable)
    {
        if (collidable.Mobility == CollidableMobility.Dynamic)
        {
            return collidable.BodyHandle.Value != BodyToIgnore.Value;
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
        if (t < maximumT)
        {
            Hit = true;
            T = t;
            Normal = normal;

            if (collidable.Mobility == CollidableMobility.Dynamic)
            {
                Body = collidable.BodyHandle;
            }

            maximumT = t;
        }
    }
}