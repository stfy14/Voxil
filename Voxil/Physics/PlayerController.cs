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

    public float WalkSpeed { get; set; } = 4.3f;
    public float SprintSpeed { get; set; } = 5.6f;
    public float JumpVelocity { get; set; } = 7.0f;

    public bool IsOnGround { get; private set; }

    private readonly PhysicsWorld _physicsWorld;
    private readonly Camera _camera;

    private float _verticalVelocity = 0f;
    private const float Gravity = 20.0f;
    private float _coyoteTime = 0f;
    private const float CoyoteTimeDuration = 0.1f;

    public PlayerController(PhysicsWorld physicsWorld, Camera camera, System.Numerics.Vector3 startPosition)
    {
        _physicsWorld = physicsWorld;
        _camera = camera;

        float radius = Width / 2f;
        var shape = new Cylinder(radius, Height);
        var shapeIndex = physicsWorld.Simulation.Shapes.Add(shape);

        // ВОЗВРАЩАЕМСЯ к динамическому телу, но с особыми настройками
        var bodyDescription = BodyDescription.CreateDynamic(
            new RigidPose(startPosition),
            new BodyInertia { InverseMass = 1f / 70f },
            new CollidableDescription(shapeIndex, 0.1f),
            new BodyActivityDescription(0.01f));

        // Блокируем вращение
        bodyDescription.LocalInertia.InverseInertiaTensor = default;

        BodyHandle = physicsWorld.Simulation.Bodies.Add(bodyDescription);

        Console.WriteLine($"[PlayerController] Создан с BodyHandle: {BodyHandle.Value}");
    }

    public void Update(InputManager input, float deltaTime)
    {
        var bodyReference = _physicsWorld.Simulation.Bodies.GetBodyReference(BodyHandle);
        var bodyPosition = bodyReference.Pose.Position;

        // Камера
        Vector2 mouseDelta = input.GetMouseDelta();
        _camera.Rotate(mouseDelta.X, mouseDelta.Y);

        // Проверяем землю
        CheckGrounded(bodyPosition);

        // === ГОРИЗОНТАЛЬНОЕ ДВИЖЕНИЕ ===
        Vector2 movementInput = input.GetMovementInput();
        System.Numerics.Vector3 desiredHorizontalVelocity = System.Numerics.Vector3.Zero;

        if (movementInput.LengthSquared > 0.01f)
        {
            var forward = System.Numerics.Vector3.Normalize(
                new System.Numerics.Vector3(_camera.Front.X, 0, _camera.Front.Z));
            var right = System.Numerics.Vector3.Normalize(
                new System.Numerics.Vector3(_camera.Right.X, 0, _camera.Right.Z));

            var moveDirection = forward * movementInput.Y + right * movementInput.X;
            moveDirection = System.Numerics.Vector3.Normalize(moveDirection);

            float speed = input.IsSprintPressed() ? SprintSpeed : WalkSpeed;
            desiredHorizontalVelocity = moveDirection * speed;
        }

        // === ВЕРТИКАЛЬНОЕ ДВИЖЕНИЕ ===
        if (IsOnGround)
        {
            _verticalVelocity = -2f; // Прижимаем к земле

            if (input.IsJumpPressed() && _coyoteTime > 0)
            {
                _verticalVelocity = JumpVelocity;
                _coyoteTime = 0;
            }
        }
        else
        {
            _verticalVelocity -= Gravity * deltaTime;
            _coyoteTime = Math.Max(0, _coyoteTime - deltaTime);
        }

        // КЛЮЧЕВОЕ: Напрямую устанавливаем скорость (override физики)
        bodyReference.Velocity.Linear = new System.Numerics.Vector3(
            desiredHorizontalVelocity.X,
            _verticalVelocity,
            desiredHorizontalVelocity.Z
        );

        // КРИТИЧНО: Обнуляем любые внешние силы
        bodyReference.Velocity.Angular = System.Numerics.Vector3.Zero;

        UpdateCameraPosition(bodyPosition);
    }

    private void CheckGrounded(System.Numerics.Vector3 bodyPosition)
    {
        var down = -System.Numerics.Vector3.UnitY;
        float rayLength = (Height / 2f) + 0.15f;

        // Проверяем центр
        bool centerHit = _physicsWorld.Raycast(
            bodyPosition, down, rayLength,
            out _, out _, out _, BodyHandle);

        // Проверяем края
        float radius = Width / 2f * 0.8f;
        bool edgeHit = false;

        var offsets = new[]
        {
            new System.Numerics.Vector3(radius, 0, 0),
            new System.Numerics.Vector3(-radius, 0, 0),
            new System.Numerics.Vector3(0, 0, radius),
            new System.Numerics.Vector3(0, 0, -radius)
        };

        foreach (var offset in offsets)
        {
            if (_physicsWorld.Raycast(
                bodyPosition + offset, down, rayLength,
                out _, out _, out _, BodyHandle))
            {
                edgeHit = true;
                break;
            }
        }

        IsOnGround = centerHit || edgeHit;

        if (IsOnGround)
        {
            _coyoteTime = CoyoteTimeDuration;
        }
    }

    private void UpdateCameraPosition(System.Numerics.Vector3 bodyPosition)
    {
        _camera.SetPosition(bodyPosition.ToOpenTK() +
            new Vector3(0, EyeHeight - Height / 2f, 0));
    }
}