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
    private float _gracePeriodTimer = 3.0f;

    // --- УДАЛЯЕМ СТАРЫЕ ПОЛЯ ---
    // public float WalkSpeed { get; set; } = 4.3f; ... и т.д.

    private readonly PhysicsWorld _physicsWorld;
    private readonly Camera _camera;
    private readonly PlayerState _playerState;

    public PlayerController(PhysicsWorld physicsWorld, Camera camera, System.Numerics.Vector3 startPosition)
    {
        _physicsWorld = physicsWorld;
        _camera = camera;
        _playerState = physicsWorld.GetPlayerState();

        // --- ГЛАВНОЕ ИЗМЕНЕНИЕ ---
        // Просто выберите нужный пресет здесь!
        SetPreset(ControllerPreset.DebugLevitate);
        // SetPreset(ControllerPreset.DebugLevitate);

        float radius = Width / 2f;
        var shape = new Capsule(radius, Height - 2 * radius);
        var shapeIndex = physicsWorld.Simulation.Shapes.Add(shape);
        var bodyDescription = BodyDescription.CreateDynamic(
            new RigidPose(startPosition),
            new BodyInertia { InverseMass = 1f / 70f },
            new CollidableDescription(shapeIndex, 0.1f),
            new BodyActivityDescription(0f));
        bodyDescription.LocalInertia.InverseInertiaTensor = default;
        BodyHandle = physicsWorld.Simulation.Bodies.Add(bodyDescription);
        Console.WriteLine($"[PlayerController] Создан с BodyHandle: {BodyHandle.Value}");
    }

    // --- НОВЫЙ МЕТОД ДЛЯ УСТАНОВКИ ПРЕСЕТА ---
    public void SetPreset(ControllerPreset preset)
    {
        switch (preset)
        {
            case ControllerPreset.Normal:
                _playerState.Settings = ControllerSettingsPresets.Normal;
                break;
            case ControllerPreset.DebugLevitate:
                _playerState.Settings = ControllerSettingsPresets.DebugLevitate;
                break;
        }
    }

    public void Update(InputManager input, float deltaTime)
    {
        var bodyReference = _physicsWorld.Simulation.Bodies.GetBodyReference(BodyHandle);
        var bodyPosition = bodyReference.Pose.Position;

        var mouseDelta = input.GetMouseDelta();
        _camera.Rotate(mouseDelta.X, mouseDelta.Y);

        // Читаем настройки из общего состояния
        var settings = _playerState.Settings;

        Vector2 movementInput = input.GetMovementInput();
        var desiredVelocity = System.Numerics.Vector2.Zero;
        if (movementInput.LengthSquared > 0.01f)
        {
            var forward = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(_camera.Front.X, 0, _camera.Front.Z));
            var right = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(_camera.Right.X, 0, _camera.Right.Z));
            var moveDirection = forward * movementInput.Y + right * movementInput.X;
            float speed = input.IsSprintPressed() ? settings.SprintSpeed : settings.WalkSpeed;
            desiredVelocity = new System.Numerics.Vector2(moveDirection.X, moveDirection.Z) * speed;
        }
        _physicsWorld.SetPlayerGoalVelocity(desiredVelocity);

        if (input.IsKeyDown(input.Jump) && _playerState.IsOnGround)
        {
            var currentVelocity = bodyReference.Velocity.Linear;
            bodyReference.Velocity.Linear = new System.Numerics.Vector3(currentVelocity.X, settings.JumpVelocity, currentVelocity.Z);
        }

        UpdateCameraPosition(bodyPosition);
    }

    private void UpdateCameraPosition(System.Numerics.Vector3 bodyPosition)
    {
        _camera.SetPosition(bodyPosition.ToOpenTK() + new Vector3(0, EyeHeight - Height / 2f, 0));
    }
}