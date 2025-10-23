// /Physics/Callbacks/NarrowPhaseCallbacks.cs - РЕКОМЕНДУЕМОЕ ИСПРАВЛЕНИЕ
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
        // Задаем базовые настройки "пружинистости" для всех контактов.
        // Это стандартная практика в BepuPhysics.
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

    // Эта версия будет вызываться для ВСЕХ пар, включая Convex-Convex.
    // Она является универсальной и более надежной.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterialProperties) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        // 1. Задаем базовые свойства для ЛЮБОГО столкновения
        pairMaterialProperties = new PairMaterialProperties
        {
            FrictionCoefficient = 1f,
            MaximumRecoveryVelocity = 2f,
            SpringSettings = ContactSpringiness
        };

        // 2. Если в столкновекновении участвует игрок, применяем особую логику
        var aIsPlayer = pair.A.BodyHandle == PlayerState.BodyHandle;
        var bIsPlayer = pair.B.BodyHandle == PlayerState.BodyHandle;
        if ((aIsPlayer || bIsPlayer) && manifold.Count > 0)
        {
            // ИСПРАВЛЕНИЕ ЗДЕСЬ:
            // Получаем нормаль для первого контакта в "манифолде".
            // Метод требует, чтобы мы передали ему 'manifold' по ссылке (ref).
            var normal = manifold.GetNormal(ref manifold, 0);

            if (bIsPlayer)
            {
                normal = -normal;
            }

            // Если нормаль направлена вверх (это пол), то увеличиваем трение
            if (normal.Y > 0.707f)
            {
                pairMaterialProperties.FrictionCoefficient = 1.0f;
            }
            else
            {
                pairMaterialProperties.FrictionCoefficient = 0.0f; // Скользкие стены
            }
        }
        return true;
    }

    // Вторая перегрузка больше не нужна, если мы используем обобщенную (дженерик) версию выше.
    // Но на всякий случай, если она будет вызвана, она тоже должна работать.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold)
    {
        return true;
    }
}