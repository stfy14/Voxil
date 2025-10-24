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

        if (!Vector.EqualsAll(playerLane, Vector<int>.Zero))
        {
            var settings = PlayerState.Settings;
            float frameDt = dt[0];

            // --- ВСЯ ВЕРТИКАЛЬНАЯ ЛОГИКА ТЕПЕРЬ ЗДЕСЬ ---
            if (PlayerState.IsOnGround)
            {
                // --- ЛОГИКА ПРУЖИНЫ (HOVER) ---
                // Вычисляем ошибку (насколько мы далеко от идеальной высоты)
                float error = PlayerState.RayT - (PlayerController.Height / 2f + settings.HoverHeight);

                // Вычисляем силу пружины, которая будет бороться с ошибкой и текущей скоростью
                float springForce = -error * settings.SpringFrequency - velocity.Linear.Y[0] * settings.SpringDamping;

                // Применяем силу как изменение скорости (ускорение)
                velocity.Linear.Y += new Vector<float>(springForce * frameDt);
            }
            else
            {
                // --- ЛОГИКА ГРАВИТАЦИИ (КОГДА В ВОЗДУХЕ) ---
                velocity.Linear.Y += settings.Gravity.Y * dt;
            }

            // --- ГОРИЗОНТАЛЬНАЯ ЛОГИКА (остается без изменений) ---
            var currentHorizontalVelocity = new Vector2(velocity.Linear.X[0], velocity.Linear.Z[0]);
            var goalHorizontalVelocity = PlayerState.GoalVelocity;
            var velocityDifference = goalHorizontalVelocity - currentHorizontalVelocity;
            var acceleration = velocityDifference * settings.MovementAcceleration;
            float accelerationMagnitude = acceleration.Length();
            if (accelerationMagnitude > settings.MovementAcceleration)
            {
                acceleration *= settings.MovementAcceleration / accelerationMagnitude;
            }
            velocity.Linear.X += new Vector<float>(acceleration.X * frameDt);
            velocity.Linear.Z += new Vector<float>(acceleration.Y * frameDt);

            if (PlayerState.GoalVelocity.LengthSquared() < 0.01f && PlayerState.IsOnGround)
            {
                float dampingFactor = (float)Math.Pow(settings.MovementDamping, frameDt);
                velocity.Linear.X *= new Vector<float>(dampingFactor);
                velocity.Linear.Z *= new Vector<float>(dampingFactor);
            }
        }
    }

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;
}