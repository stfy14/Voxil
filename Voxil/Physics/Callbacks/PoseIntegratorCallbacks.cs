// /Physics/Callbacks/PoseIntegratorCallbacks.cs
using BepuPhysics;
using BepuUtilities;
using System.Numerics;
using System.Runtime.CompilerServices;

public struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    public PlayerState PlayerState;
    public PhysicsWorld World;

    // --- УДАЛЯЕМ ВСЕ СТАРЫЕ ПОЛЯ НАСТРОЕК ---
    // public Vector3 Gravity;
    // public float MovementDamping; ... и т.д.

    public void Initialize(Simulation simulation) { }
    public void PrepareForIntegration(float dt) { } // Больше ничего не нужно готовить

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
    {
        var playerLane = Vector.Equals(bodyIndices, new Vector<int>(PlayerState.BodyHandle.Value));

        // --- ЧИТАЕМ НАСТРОЙКИ В НАЧАЛЕ ---
        var settings = PlayerState.Settings;
        var gravityWide = Vector3Wide.Broadcast(settings.Gravity);

        velocity.Linear += Vector3Wide.ConditionalSelect(playerLane, default, gravityWide * dt);

        if (!Vector.EqualsAll(playerLane, Vector<int>.Zero))
        {
            float frameDt = dt[0];
            var bodyPosition = new Vector3(position.X[0], position.Y[0], position.Z[0]);

            float rayLength = PlayerController.Height / 2f + settings.HoverHeight + 0.2f;
            var hitHandler = new VoxelHitHandler { PlayerBodyHandle = PlayerState.BodyHandle, Simulation = World.Simulation };
            World.Simulation.RayCast(bodyPosition, -Vector3.UnitY, rayLength, ref hitHandler);
            PlayerState.IsOnGround = hitHandler.Hit;

            if (PlayerState.IsOnGround)
            {
                float error = hitHandler.T - (PlayerController.Height / 2f + settings.HoverHeight);
                float springForce = -error * settings.SpringFrequency - velocity.Linear.Y[0] * settings.SpringDamping;
                velocity.Linear.Y += new Vector<float>(springForce * frameDt);
            }
            else
            {
                velocity.Linear.Y += new Vector<float>(settings.Gravity.Y * frameDt);
            }

            Vector2 goalVelocity = PlayerState.GoalVelocity;
            var currentHorizontalVelocity = new Vector2(velocity.Linear.X[0], velocity.Linear.Z[0]);
            var velocityDifference = goalVelocity - currentHorizontalVelocity;
            var acceleration = velocityDifference * settings.MovementAcceleration;
            float maxAccel = 100.0f;
            if (acceleration.LengthSquared() > maxAccel * maxAccel)
            {
                acceleration = Vector2.Normalize(acceleration) * maxAccel;
            }
            var impulse = new Vector3Wide { X = new Vector<float>(acceleration.X * frameDt), Y = Vector<float>.Zero, Z = new Vector<float>(acceleration.Y * frameDt) };
            velocity.Linear += impulse;

            if (goalVelocity.LengthSquared() < 0.01f)
            {
                float dampingFactor = 1.0f - (1.0f - settings.MovementDamping) * frameDt;
                velocity.Linear.X *= new Vector<float>(dampingFactor);
                velocity.Linear.Z *= new Vector<float>(dampingFactor);
            }
        }
    }

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;
}