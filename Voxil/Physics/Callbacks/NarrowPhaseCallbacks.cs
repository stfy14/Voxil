// /Physics/Callbacks/NarrowPhaseCallbacks.cs - ИСПРАВЛЕН ДЛЯ BEPU v2.5
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using System.Numerics;
using System.Runtime.CompilerServices;

public struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public PlayerState PlayerState;
    public SpringSettings ContactSpringiness;

    public void Initialize(Simulation simulation)
    {
        ContactSpringiness = new SpringSettings(30, 1);
    }

    public void Dispose() { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
    {
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterialProperties) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterialProperties = new PairMaterialProperties
        {
            FrictionCoefficient = 1f,
            MaximumRecoveryVelocity = 2f,
            SpringSettings = ContactSpringiness
        };

        var aIsPlayer = pair.A.BodyHandle == PlayerState.BodyHandle;
        var bIsPlayer = pair.B.BodyHandle == PlayerState.BodyHandle;
        if ((aIsPlayer || bIsPlayer) && manifold.Count > 0)
        {
            // ИСПРАВЛЕНИЕ v2.5: Метод GetNormal теперь принимает только один аргумент - индекс.
            var normal = manifold.GetNormal(0);

            if (bIsPlayer)
            {
                normal = -normal;
            }

            if (normal.Y > 0.707f)
            {
                pairMaterialProperties.FrictionCoefficient = 1.0f;
            }
            else
            {
                pairMaterialProperties.FrictionCoefficient = 0.0f;
            }
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold)
    {
        return true;
    }
}