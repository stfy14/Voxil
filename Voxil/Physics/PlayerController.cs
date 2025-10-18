// /Physics/PlayerController.cs
using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Numerics;
using Vector2 = OpenTK.Mathematics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;

public class PlayerController
{
    public BodyHandle BodyHandle { get; }

    public const float Height = 1.8f;
    public const float Width = 0.6f;
    public const float EyeHeight = 1.62f;

    public float WalkSpeed { get; set; } = 5.0f;
    public float SprintSpeed { get; set; } = 8.0f;
    public float JumpVelocity { get; set; } = 6.0f;

    public bool IsOnGround { get; private set; }

    private readonly PhysicsWorld _physicsWorld;
    private readonly Camera _camera;

    public PlayerController(PhysicsWorld physicsWorld, Camera camera, System.Numerics.Vector3 startPosition)
    {
        _physicsWorld = physicsWorld;
        _camera = camera;

        float radius = Width / 2f;

        // ИСПРАВЛЕНИЕ №1: Высота цилиндра должна быть равна полной высоте игрока.
        var shape = new Cylinder(radius, Height);

        var shapeIndex = physicsWorld.Simulation.Shapes.Add(shape);
        var activityDescription = new BodyActivityDescription(0f);

        var bodyDescription = BodyDescription.CreateDynamic(
            new RigidPose(startPosition),
            new BodyInertia { InverseMass = 1f / 70f },
            new CollidableDescription(shapeIndex, 0.1f),
            activityDescription);

        bodyDescription.LocalInertia.InverseInertiaTensor = default;
        BodyHandle = physicsWorld.Simulation.Bodies.Add(bodyDescription);
    }

    private bool IsObstacleInDirection(System.Numerics.Vector3 direction)
    {
        var bodyReference = _physicsWorld.Simulation.Bodies.GetBodyReference(BodyHandle);
        var bodyPosition = bodyReference.Pose.Position;
        float probeDistance = Width / 2f + 0.05f;

        // Щуп №1: На уровне центра тела (остается без изменений)
        if (_physicsWorld.Raycast(bodyPosition, direction, probeDistance, out _, out _, out _, BodyHandle))
        {
            return true;
        }

        // --- ИСПРАВЛЕНИЕ №2: ПРАВИЛЬНЫЙ РАСЧЕТ ПОЛОЖЕНИЯ НИЖНЕГО ЛУЧА ---
        // Цель: луч должен начинаться ВНУТРИ коллайдера, но очень близко к его дну.
        // 1. Находим самую нижнюю точку коллайдера: bodyPosition.Y - Height / 2
        // 2. Прибавляем небольшой зазор (например, 0.2f), чтобы луч начался чуть выше.
        float bottomY = bodyPosition.Y - (Height / 2f);
        float feetProbeY = bottomY + 0.2f;

        var feetProbeStart = new System.Numerics.Vector3(bodyPosition.X, feetProbeY, bodyPosition.Z);

        if (_physicsWorld.Raycast(feetProbeStart, direction, probeDistance, out _, out _, out _, BodyHandle))
        {
            return true; // Найдено препятствие на уровне ног
        }

        return false;
    }

    public void Update(InputManager input)
    {
        var bodyReference = _physicsWorld.Simulation.Bodies.GetBodyReference(BodyHandle);
        var bodyPosition = bodyReference.Pose.Position;

        Vector2 mouseDelta = input.GetMouseDelta();
        _camera.Rotate(mouseDelta.X, mouseDelta.Y);

        Vector2 movementInput = input.GetMovementInput();

        var forward = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(_camera.Front.X, 0, _camera.Front.Z));
        var right = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(_camera.Right.X, 0, _camera.Right.Z));

        var forwardVelocity = forward * movementInput.Y;
        var rightVelocity = right * movementInput.X;

        if (movementInput.Y != 0)
        {
            var moveDir = forward * Math.Sign(movementInput.Y);
            if (IsObstacleInDirection(moveDir))
            {
                forwardVelocity = System.Numerics.Vector3.Zero;
            }
        }

        if (movementInput.X != 0)
        {
            var moveDir = right * Math.Sign(movementInput.X);
            if (IsObstacleInDirection(moveDir))
            {
                rightVelocity = System.Numerics.Vector3.Zero;
            }
        }

        var desiredVelocity = forwardVelocity + rightVelocity;

        if (desiredVelocity.LengthSquared() > 0)
        {
            float speed = input.IsSprintPressed() ? SprintSpeed : WalkSpeed;
            desiredVelocity = System.Numerics.Vector3.Normalize(desiredVelocity) * speed;
        }

        var currentVerticalVelocity = bodyReference.Velocity.Linear.Y;
        bodyReference.Velocity.Linear = new System.Numerics.Vector3(desiredVelocity.X, currentVerticalVelocity, desiredVelocity.Z);

        CheckGrounded(bodyPosition);

        if (input.IsJumpPressed() && IsOnGround)
        {
            var vel = bodyReference.Velocity.Linear;
            vel.Y = JumpVelocity;
            bodyReference.Velocity.Linear = vel;
        }

        UpdateCameraPosition(bodyPosition);
    }

    private void CheckGrounded(System.Numerics.Vector3 bodyPosition)
    {
        var down = -System.Numerics.Vector3.UnitY;
        // Для цилиндра лучше сделать луч для проверки земли чуть длиннее
        float rayLength = (Height / 2f) + 0.1f;
        IsOnGround = _physicsWorld.Raycast(bodyPosition, down, rayLength, out _, out _, out _, BodyHandle);
    }

    private void UpdateCameraPosition(System.Numerics.Vector3 bodyPosition)
    {
        _camera.SetPosition(bodyPosition.ToOpenTK() + new Vector3(0, EyeHeight - Height / 2f, 0));
    }
}