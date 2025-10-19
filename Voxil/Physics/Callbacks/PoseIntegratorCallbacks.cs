// /Physics/Callbacks/PoseIntegratorCallbacks.cs
using BepuPhysics;
using BepuUtilities;
using System.Numerics;
using System.Runtime.CompilerServices;

public struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    public Vector3 Gravity;
    private Vector3Wide GravityWide;
    public PlayerState PlayerState; // Ссылка на общий объект состояния
    public float MovementDamping;
    public float MovementAcceleration;

    public void Initialize(Simulation simulation)
    {
        Gravity = new Vector3(0, -20, 0);
        MovementDamping = 0.92f;
        MovementAcceleration = 300f;
    }

    public void PrepareForIntegration(float dt)
    {
        GravityWide = Vector3Wide.Broadcast(Gravity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
    {
        velocity.Linear += GravityWide * dt;

        // Читаем PlayerHandle из общего состояния
        var playerLane = Vector.Equals(bodyIndices, new Vector<int>(PlayerState.BodyHandle.Value));

        if (!Vector.EqualsAll(playerLane, Vector<int>.Zero))
        {
            // Читаем GoalVelocity из общего состояния
            Vector2 goalVelocity = PlayerState.GoalVelocity;

            var currentHorizontalVelocity = new Vector2(velocity.Linear.X[0], velocity.Linear.Z[0]);
            var velocityDifference = goalVelocity - currentHorizontalVelocity;
            var acceleration = velocityDifference * MovementAcceleration;
            float maxAccel = 50.0f;
            if (acceleration.LengthSquared() > maxAccel * maxAccel)
            {
                acceleration = Vector2.Normalize(acceleration) * maxAccel;
            }

            Vector3Wide impulse;
            impulse.X = new Vector<float>(acceleration.X * dt[0]);
            impulse.Y = Vector<float>.Zero;
            impulse.Z = new Vector<float>(acceleration.Y * dt[0]);
            velocity.Linear += Vector3Wide.ConditionalSelect(playerLane, impulse, default);

            if (goalVelocity.LengthSquared() < 0.01f)
            {
                float dampingFactor = 1.0f - (1.0f - MovementDamping) * dt[0];
                velocity.Linear.X *= new Vector<float>(dampingFactor);
                velocity.Linear.Z *= new Vector<float>(dampingFactor);
            }
        }
    }
    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;
}