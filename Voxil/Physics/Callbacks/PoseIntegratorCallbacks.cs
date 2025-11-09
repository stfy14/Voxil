// /Physics/Callbacks/PoseIntegratorCallbacks.cs - ВАША СТАРАЯ ЛОГИКА, ИНТЕГРИРОВАННАЯ ПРАВИЛЬНО

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
        // 1. Определяем, в каком "канале" SIMD-вектора находится игрок.
        var playerLane = Vector.Equals(bodyIndices, new Vector<int>(this.PlayerState.BodyHandle.Value));

        // Если игрока в этой пачке нет, применяем только гравитацию и выходим.
        if (Vector.EqualsAll(playerLane, Vector<int>.Zero))
        {
            velocity.Linear.Y += new Vector<float>(this.PlayerState.Settings.Gravity.Y) * dt;
            return;
        }

        // --- ЕСЛИ ИГРОК В ПАЧКЕ ---
        // Получаем скалярные значения скорости ИМЕННО игрока, а не [0] элемента
        var playerVelX = Vector.Dot(velocity.Linear.X, Vector.ConditionalSelect(playerLane, Vector<float>.One, Vector<float>.Zero));
        var playerVelY = Vector.Dot(velocity.Linear.Y, Vector.ConditionalSelect(playerLane, Vector<float>.One, Vector<float>.Zero));
        var playerVelZ = Vector.Dot(velocity.Linear.Z, Vector.ConditionalSelect(playerLane, Vector<float>.One, Vector<float>.Zero));

        var settings = this.PlayerState.Settings;
        float frameDt = dt[0];

        // --- ВЕРТИКАЛЬНАЯ ЛОГИКА ИЗ ВАШЕГО СТАРОГО КОДА ---
        Vector<float> playerTargetVelocityY;
        if (this.PlayerState.IsOnGround)
        {
            float error = this.PlayerState.RayT - (PlayerController.Height / 2f + settings.HoverHeight);
            float springForce = -error * settings.SpringFrequency - playerVelY * settings.SpringDamping;
            playerTargetVelocityY = velocity.Linear.Y + new Vector<float>(springForce * frameDt);
            // ПРИМЕЧАНИЕ: Гравитация здесь НЕ добавляется, т.к. пружина сама должна ее компенсировать.
        }
        else
        {
            // В воздухе на игрока действует только гравитация.
            playerTargetVelocityY = velocity.Linear.Y + new Vector<float>(settings.Gravity.Y * frameDt);
        }

        // --- ГОРИЗОНТАЛЬНАЯ ЛОГИКА ИЗ ВАШЕГО СТАРОГО КОДА ---
        Vector2 goalVelocity = this.PlayerState.GoalVelocity;
        var currentHorizontalVelocity = new Vector2(playerVelX, playerVelZ);
        var velocityDifference = goalVelocity - currentHorizontalVelocity;
        var acceleration = velocityDifference * settings.MovementAcceleration;

        // Ограничение ускорения (в вашем старом коде было, но не использовалось, можно добавить при желании)
        // float maxAccel = 100.0f; ...

        var impulseX = new Vector<float>(acceleration.X * frameDt);
        var impulseZ = new Vector<float>(acceleration.Y * frameDt);

        var playerTargetVelocityX = velocity.Linear.X + impulseX;
        var playerTargetVelocityZ = velocity.Linear.Z + impulseZ;

        // --- ЛОГИКА ДЕМПФИРОВАНИЯ ИЗ ВАШЕГО СТАРОГО КОДА ---
        if (goalVelocity.LengthSquared() < 0.01f && this.PlayerState.IsOnGround)
        {
            // Используем более стабильную экспоненциальную модель, которую подразумевают ваши настройки
            float dampingFactor = (float)Math.Pow(settings.MovementDamping, frameDt);
            playerTargetVelocityX *= new Vector<float>(dampingFactor);
            playerTargetVelocityZ *= new Vector<float>(dampingFactor);
        }

        // --- ФИНАЛЬНАЯ СБОРКА РЕЗУЛЬТАТА ---

        // Целевая скорость для всех остальных объектов - просто гравитация
        var otherTargetVelocityY = velocity.Linear.Y + new Vector<float>(settings.Gravity.Y * frameDt);

        // Собираем итоговый вектор: для каналов игрока берем рассчитанные значения, для остальных - стандартные
        velocity.Linear.X = Vector.ConditionalSelect(playerLane, playerTargetVelocityX, velocity.Linear.X);
        velocity.Linear.Y = Vector.ConditionalSelect(playerLane, playerTargetVelocityY, otherTargetVelocityY);
        velocity.Linear.Z = Vector.ConditionalSelect(playerLane, playerTargetVelocityZ, velocity.Linear.Z);
    }

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;
}