// /Physics/Callbacks/PoseIntegratorCallbacks.cs
using BepuPhysics;
using BepuUtilities;
using System;
using System.Numerics;

public struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    public PlayerState PlayerState;

    public void Initialize(Simulation simulation) { }
    public void PrepareForIntegration(float dt) { }

    public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
    {
        // 1. Определяем, есть ли игрок в этой пачке
        var playerLane = Vector.Equals(bodyIndices, new Vector<int>(this.PlayerState.BodyHandle.Value));

        // Если игрока нет, применяем гравитацию ко всем и выходим (стандартное поведение)
        if (Vector.EqualsAll(playerLane, Vector<int>.Zero))
        {
            velocity.Linear.Y += new Vector<float>(this.PlayerState.Settings.Gravity.Y) * dt;
            return;
        }

        // --- ИСПРАВЛЕНИЕ: Если игрок летит, мы пропускаем расчеты физики для него ---
        if (this.PlayerState.IsFlying)
        {
            // Для всех тел В ПАЧКЕ, КОТОРЫЕ НЕ ЯВЛЯЮТСЯ ИГРОКОМ, нужно применить гравитацию.
            // А скорость игрока оставляем как есть (ее задал контроллер).
            var gravity = new Vector<float>(this.PlayerState.Settings.Gravity.Y) * dt;
            
            // Если в lane не игрок -> применяем гравитацию. Если игрок -> 0.
            var gravityToApply = Vector.ConditionalSelect(playerLane, Vector<float>.Zero, gravity);
            
            velocity.Linear.Y += gravityToApply;
            return; 
        }

        // ... ДАЛЕЕ СТАРЫЙ КОД ДЛЯ ОБЫЧНОЙ ХОДЬБЫ ...

        var playerVelX = Vector.Dot(velocity.Linear.X, Vector.ConditionalSelect(playerLane, Vector<float>.One, Vector<float>.Zero));
        var playerVelY = Vector.Dot(velocity.Linear.Y, Vector.ConditionalSelect(playerLane, Vector<float>.One, Vector<float>.Zero));
        var playerVelZ = Vector.Dot(velocity.Linear.Z, Vector.ConditionalSelect(playerLane, Vector<float>.One, Vector<float>.Zero));

        var settings = this.PlayerState.Settings;
        float frameDt = dt[0];

        Vector<float> playerTargetVelocityY;
        if (this.PlayerState.IsOnGround)
        {
            float error = this.PlayerState.RayT - (PlayerController.Height / 2f + settings.HoverHeight);
            float springForce = -error * settings.SpringFrequency - playerVelY * settings.SpringDamping;
            playerTargetVelocityY = velocity.Linear.Y + new Vector<float>(springForce * frameDt);
        }
        else
        {
            playerTargetVelocityY = velocity.Linear.Y + new Vector<float>(settings.Gravity.Y * frameDt);
        }

        Vector2 goalVelocity = this.PlayerState.GoalVelocity;
        var currentHorizontalVelocity = new Vector2(playerVelX, playerVelZ);
        var velocityDifference = goalVelocity - currentHorizontalVelocity;
        var acceleration = velocityDifference * settings.MovementAcceleration;

        var impulseX = new Vector<float>(acceleration.X * frameDt);
        var impulseZ = new Vector<float>(acceleration.Y * frameDt);

        var playerTargetVelocityX = velocity.Linear.X + impulseX;
        var playerTargetVelocityZ = velocity.Linear.Z + impulseZ;

        if (goalVelocity.LengthSquared() < 0.01f && this.PlayerState.IsOnGround)
        {
            float dampingFactor = (float)Math.Pow(settings.MovementDamping, frameDt);
            playerTargetVelocityX *= new Vector<float>(dampingFactor);
            playerTargetVelocityZ *= new Vector<float>(dampingFactor);
        }

        var otherTargetVelocityY = velocity.Linear.Y + new Vector<float>(settings.Gravity.Y * frameDt);

        velocity.Linear.X = Vector.ConditionalSelect(playerLane, playerTargetVelocityX, velocity.Linear.X);
        velocity.Linear.Y = Vector.ConditionalSelect(playerLane, playerTargetVelocityY, otherTargetVelocityY);
        velocity.Linear.Z = Vector.ConditionalSelect(playerLane, playerTargetVelocityZ, velocity.Linear.Z);
    }

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;
}