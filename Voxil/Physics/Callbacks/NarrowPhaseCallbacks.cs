// /Physics/Callbacks/NarrowPhaseCallbacks.cs
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using System.Runtime.CompilerServices;

public struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public SpringSettings SpringSettings;

    public void Initialize(Simulation simulation)
    {
        // Увеличиваем жёсткость (frequency) и демпфирование для более стабильных коллизий
        SpringSettings = new SpringSettings(60, 5); // Было (30, 1)
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        // Увеличиваем speculative margin для объектов, которые могут двигаться быстро
        if (a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic)
        {
            speculativeMargin = 1.0f; // Увеличиваем для динамических объектов
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
        pairMaterialProperties = new PairMaterialProperties
        {
            FrictionCoefficient = 1f,
            MaximumRecoveryVelocity = 2f,
            SpringSettings = this.SpringSettings
        };

        bool isDynamic = pair.A.Mobility == CollidableMobility.Dynamic || pair.B.Mobility == CollidableMobility.Dynamic;
        if (isDynamic && manifold.Count > 0)
        {
            var normal = manifold.GetNormal(ref manifold, 0);
            // Все, что круче ~45 градусов, считаем стеной
            if (System.Math.Abs(normal.Y) < 0.707f)
            {
                // СТЕНА: Устанавливаем небольшое трение.
                // Достаточно, чтобы не скользить бесконтрольно,
                // но недостаточно, чтобы "прилипнуть" или вызвать "трамплин" с новым контроллером.
                pairMaterialProperties.FrictionCoefficient = 0.2f;
            }
            else
            {
                // ПОЛ: Полное трение.
                pairMaterialProperties.FrictionCoefficient = 1f;
            }
        }
        return true;
    }

    public void Dispose() { }
}