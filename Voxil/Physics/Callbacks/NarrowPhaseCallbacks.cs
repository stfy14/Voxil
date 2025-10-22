// /Physics/Callbacks/NarrowPhaseCallbacks.cs - FIXED
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using System.Numerics;
using System.Runtime.CompilerServices;

public struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public PlayerState PlayerState;

    public void Initialize(Simulation simulation) { }
    public void Dispose() { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        // Разрешаем все контакты
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
    {
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(
        int workerIndex,
        CollidablePair pair,
        ref TManifold manifold,
        out PairMaterialProperties pairMaterialProperties) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        // Проверяем, участвует ли игрок в контакте
        var aIsPlayer = pair.A.Mobility == CollidableMobility.Dynamic && pair.A.BodyHandle == PlayerState.BodyHandle;
        var bIsPlayer = pair.B.Mobility == CollidableMobility.Dynamic && pair.B.BodyHandle == PlayerState.BodyHandle;

        if (aIsPlayer || bIsPlayer)
        {
            // Инициализируем дефолтные значения
            float friction = 0.0f;

            // ИСПРАВЛЕНИЕ: Проверяем наличие точек контакта
            if (manifold.Count > 0)
            {
                // ПРАВИЛЬНЫЙ способ получения нормали из манифолда
                // GetContact требует все out параметры
                manifold.GetContact(0, out var offset, out var depth, out var featureId, out var contactIndex);

                // Вычисляем нормаль из offset
                Vector3 normal;
                if (offset.LengthSquared() > 0.0001f)
                {
                    normal = Vector3.Normalize(offset);
                }
                else
                {
                    // Если offset нулевой, используем направление вверх как fallback
                    normal = Vector3.UnitY;
                }

                // Если игрок - это объект B, инвертируем нормаль
                // (нормаль всегда направлена от A к B)
                if (bIsPlayer)
                {
                    normal = -normal;
                }

                // Проверяем, направлена ли нормаль вверх (это пол)
                // cos(45°) ≈ 0.707
                if (normal.Y > 0.707f)
                {
                    friction = 1.0f; // Высокое трение для ходьбы по полу
                }
            }

            pairMaterialProperties = new PairMaterialProperties
            {
                FrictionCoefficient = friction,
                MaximumRecoveryVelocity = 2f,
                SpringSettings = new SpringSettings(30, 1)
            };
        }
        else
        {
            // Стандартные свойства для объектов без игрока
            pairMaterialProperties = new PairMaterialProperties
            {
                FrictionCoefficient = 1f,
                MaximumRecoveryVelocity = 2f,
                SpringSettings = new SpringSettings(30, 1)
            };
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(
        int workerIndex,
        CollidablePair pair,
        int childIndexA,
        int childIndexB,
        ref ConvexContactManifold manifold)
    {
        return true;
    }
}