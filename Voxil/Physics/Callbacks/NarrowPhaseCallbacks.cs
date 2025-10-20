// /Physics/Callbacks/NarrowPhaseCallbacks.cs
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using System.Numerics;
using System.Runtime.CompilerServices;

public struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public PlayerState PlayerState;

    public void Initialize(Simulation? simulation) { }
    public void Dispose() { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        if (PlayerState == null)
        {
            return true;
        }
        var playerHandle = PlayerState.BodyHandle;
        var aIsPlayer = a.Mobility == CollidableMobility.Dynamic && a.BodyHandle.Value == playerHandle.Value;
        var bIsPlayer = b.Mobility == CollidableMobility.Dynamic && b.BodyHandle.Value == playerHandle.Value;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterialProperties) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        var aIsPlayer = pair.A.BodyHandle == PlayerState.BodyHandle;
        var bIsPlayer = pair.B.BodyHandle == PlayerState.BodyHandle;

        if (aIsPlayer || bIsPlayer)
        {
            // Устанавливаем свойства по умолчанию на случай, если нет точек контакта
            float friction = 0.0f;

            // --- НАЧАЛО ИСПРАВЛЕНИЯ ---
            // Проверяем, что есть хотя бы одна точка контакта
            if (manifold.Count > 0)
            {
                // Вызываем метод GetNormal, передавая саму структуру manifold по ссылке (ref)
                // и индекс нужной точки контакта (в данном случае, первой - 0).
                // Это правильный синтаксис, который ожидает BepuPhysics.
                Vector3 normal = manifold.GetNormal(ref manifold, 0);

                // Нормаль всегда направлена от объекта A к объекту B.
                // Если игрок - это объект A, нам нужно инвертировать нормаль,
                // чтобы она всегда указывала "от" поверхности, с которой мы столкнулись.
                if (aIsPlayer)
                {
                    normal = -normal;
                }

                // Если Y-компонент нормали смотрит вверх (больше ~cos(45)), это пол.
                if (normal.Y > 0.707f)
                {
                    friction = 1.0f; // Высокое трение для пола
                }
            }
            // --- КОНЕЦ ИСПРАВЛЕНИЯ ---

            pairMaterialProperties = new PairMaterialProperties
            {
                FrictionCoefficient = friction, // Применяем вычисленное трение
                MaximumRecoveryVelocity = 2f,
                SpringSettings = new SpringSettings(30, 1)
            };
        }
        else
        {
            // Стандартные свойства для всех остальных объектов
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
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;
}