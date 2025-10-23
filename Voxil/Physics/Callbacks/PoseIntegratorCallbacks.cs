using BepuPhysics;
using BepuUtilities;
using System.Numerics;

public struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    public PlayerState PlayerState;

    public void Initialize(Simulation simulation) { }

    // PrepareForIntegration больше не нужен, состояние IsOnGround устанавливается извне
    public void PrepareForIntegration(float dt) { }

    public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
    {
        var playerLane = Vector.Equals(bodyIndices, new Vector<int>(PlayerState.BodyHandle.Value));

        // --- ЛОГИКА ДЛЯ ИГРОКА ---
        if (!Vector.EqualsAll(playerLane, Vector<int>.Zero))
        {
            var settings = PlayerState.Settings;

            // 1. ГРАВИТАЦИЯ: Применяем гравитацию ТОЛЬКО ЕСЛИ игрок НЕ на земле.
            if (!PlayerState.IsOnGround)
            {
                velocity.Linear.Y += settings.Gravity.Y * dt;
            }

            // 2. ГОРИЗОНТАЛЬНОЕ ДВИЖЕНИЕ
            Vector2 goalVelocity = PlayerState.GoalVelocity;
            float frameDt = dt[0];
            var currentHorizontalVelocity = new Vector2(velocity.Linear.X[0], velocity.Linear.Z[0]);
            var velocityDifference = goalVelocity - currentHorizontalVelocity;

            if (goalVelocity.LengthSquared() < 0.01f) // Демпфирование при отсутствии ввода
            {
                float dampingFactor = 1.0f - (1.0f - settings.MovementDamping) * frameDt;
                velocity.Linear.X *= new Vector<float>(dampingFactor);
                velocity.Linear.Z *= new Vector<float>(dampingFactor);
            }
            else // Ускорение при наличии ввода
            {
                var acceleration = velocityDifference * settings.MovementAcceleration;
                var impulse = new Vector3Wide { X = new Vector<float>(acceleration.X * frameDt), Y = Vector<float>.Zero, Z = new Vector<float>(acceleration.Y * frameDt) };
                velocity.Linear += impulse;
            }
        }
    }

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;
}