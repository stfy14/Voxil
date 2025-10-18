// /Physics/Callbacks/NarrowPhaseCallbacks.cs
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using System.Runtime.CompilerServices;

public struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public SpringSettings SpringSettings;
    public PlayerState PlayerState;

    public void Initialize(Simulation simulation)
    {
        SpringSettings = new SpringSettings(240, 20);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        if (a.Mobility == CollidableMobility.Dynamic && b.Mobility == CollidableMobility.Dynamic)
        {
            // Читаем из общего состояния
            if ((a.BodyHandle.Value == PlayerState.BodyHandle.Value) ||
                (b.BodyHandle.Value == PlayerState.BodyHandle.Value))
            {
                return false;
            }
        }

        // Минимальный speculative margin
        if (a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic)
        {
            speculativeMargin = 0.01f;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterialProperties) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        bool isPlayerInvolved = (pair.A.Mobility == CollidableMobility.Dynamic &&
                                  pair.A.BodyHandle.Value == PlayerState.BodyHandle.Value) ||
                                (pair.B.Mobility == CollidableMobility.Dynamic &&
                                  pair.B.BodyHandle.Value == PlayerState.BodyHandle.Value);

        if (isPlayerInvolved)
        {
            // СПЕЦИАЛЬНЫЕ настройки для игрока
            pairMaterialProperties = new PairMaterialProperties
            {
                // КРИТИЧНО: MaximumRecoveryVelocity должен быть ОЧЕНЬ низким
                MaximumRecoveryVelocity = 0.1f,

                // ОЧЕНЬ жесткие контакты
                SpringSettings = new SpringSettings(300, 25),

                FrictionCoefficient = 0.0f
            };

            if (manifold.Count > 0)
            {
                var normal = manifold.GetNormal(ref manifold, 0);

                // Только на полу есть трение
                if (normal.Y > 0.7f)
                {
                    pairMaterialProperties.FrictionCoefficient = 1.0f;
                }
            }
        }
        else
        {
            // Для осколков
            pairMaterialProperties = new PairMaterialProperties
            {
                FrictionCoefficient = 0.5f,
                MaximumRecoveryVelocity = 2f,
                SpringSettings = this.SpringSettings
            };
        }

        return true;
    }

    public void Dispose() { }
}