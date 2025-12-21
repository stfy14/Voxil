// /Physics/PlayerController.cs
using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework; 
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

    private readonly PhysicsWorld _physicsWorld;
    private readonly Camera _camera;
    private readonly PlayerState _playerState;

    private bool _isFlying = false;

    // Скорости для полета (можно вынести в Settings, но для простоты тут)
    private const float FlySpeedNormal = 15.0f;
    private const float FlySpeedFast = 50.0f;

    public PlayerController(PhysicsWorld physicsWorld, Camera camera, System.Numerics.Vector3 startPosition)
    {
        _physicsWorld = physicsWorld;
        _camera = camera;
        _playerState = physicsWorld.GetPlayerState();

        SetPreset(ControllerPreset.Normal);

        float radius = Width / 2f;
        var shape = new Capsule(radius, Height - 2 * radius);
        var shapeIndex = physicsWorld.Simulation.Shapes.Add(shape);
        
        // --- ИСПРАВЛЕНИЕ ЗДЕСЬ ---
        // Было: new BodyActivityDescription(0.01f)
        // Стало: new BodyActivityDescription(-1)
        // Значение -1 говорит движку: "Это тело всегда активно, не усыпляй его".
        var bodyDescription = BodyDescription.CreateDynamic(
            new RigidPose(startPosition),
            new BodyInertia { InverseMass = 1f / 70f },
            new CollidableDescription(shapeIndex, 0.1f),
            new BodyActivityDescription(-1)); 
        
        bodyDescription.LocalInertia.InverseInertiaTensor = default;
        BodyHandle = physicsWorld.Simulation.Bodies.Add(bodyDescription);
    }

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
            case ControllerPreset.Spectator:
                _playerState.Settings = ControllerSettingsPresets.Spectator;
                break;
        }
    }

    public void Update(InputManager input, float deltaTime)
    {
        var mouseDelta = input.GetMouseDelta();
        _camera.Rotate(mouseDelta.X, mouseDelta.Y);

        // Переключение режима полета
        if (input.IsKeyPressed(Keys.F))
        {
            _isFlying = !_isFlying;
            
            // Сообщаем состоянию (и колбеку физики), что мы летим
            _playerState.IsFlying = _isFlying;

            // Будим тело, если оно спало
            _physicsWorld.Simulation.Awakener.AwakenBody(BodyHandle);
            
            // Сбрасываем инерцию при переключении
            var refBody = _physicsWorld.Simulation.Bodies.GetBodyReference(BodyHandle);
            refBody.Velocity.Linear = System.Numerics.Vector3.Zero; 

            Console.WriteLine($"[Mode] Flying: {_isFlying}");
        }

        var bodyReference = _physicsWorld.Simulation.Bodies.GetBodyReference(BodyHandle);
        var settings = _playerState.Settings;
        Vector2 movementInput = input.GetMovementInput();

        if (_isFlying)
        {
            // --- ЛОГИКА ПОЛЕТА ---
            float currentFlySpeed = input.IsSprintPressed() ? FlySpeedFast : FlySpeedNormal;
            
            // 1. Горизонтальное движение (Плоское, "как игрок")
            // Берем Forward камеры, но обнуляем Y и нормализуем.
            var camFront = _camera.Front;
            var flatForward = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(camFront.X, 0, camFront.Z));
            
            // Right вектор уже обычно горизонтален, но для надежности тоже берем с камеры
            var camRight = _camera.Right;
            var flatRight = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(camRight.X, 0, camRight.Z));

            System.Numerics.Vector3 targetVel = System.Numerics.Vector3.Zero;

            // W/S двигают по flatForward, A/D по flatRight
            if (movementInput.LengthSquared > 0.01f)
            {
                targetVel += flatForward * movementInput.Y; 
                targetVel += flatRight * movementInput.X;
            }

            // 2. Вертикальное движение (Space / Shift) - строго по мировой оси Y
            if (input.IsKeyDown(input.Jump)) targetVel += System.Numerics.Vector3.UnitY;
            if (input.IsKeyDown(input.Crouch)) targetVel -= System.Numerics.Vector3.UnitY; // Crouch = LeftShift

            // Нормализация скорости
            if (targetVel.LengthSquared() > 0.01f)
            {
                // Если жмем кнопки, летим с заданной скоростью
                targetVel = System.Numerics.Vector3.Normalize(targetVel) * currentFlySpeed;
                bodyReference.Velocity.Linear = targetVel;
            }
            else
            {
                // Если кнопки не жмем - мгновенная остановка (зависание в воздухе)
                bodyReference.Velocity.Linear = System.Numerics.Vector3.Zero;
            }

            // ВАЖНО: Обнуляем GoalVelocity для старого контроллера, чтобы не путать логику (хотя IsFlying уже защищает)
            _physicsWorld.SetPlayerGoalVelocity(System.Numerics.Vector2.Zero);
        }
        else
        {
            // --- ЛОГИКА ХОДЬБЫ (СТАРАЯ) ---
            float speed = input.IsSprintPressed() ? settings.SprintSpeed : settings.WalkSpeed;
            
            var forward = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(_camera.Front.X, 0, _camera.Front.Z));
            var right = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(_camera.Right.X, 0, _camera.Right.Z));
            var moveDirection = forward * movementInput.Y + right * movementInput.X;
            
            var desiredVelocity = new System.Numerics.Vector2(moveDirection.X, moveDirection.Z) * speed;
            _physicsWorld.SetPlayerGoalVelocity(desiredVelocity);

            if (input.IsKeyDown(input.Jump) && _playerState.IsOnGround)
            {
                var currentVelocity = bodyReference.Velocity.Linear;
                bodyReference.Velocity.Linear = new System.Numerics.Vector3(currentVelocity.X, settings.JumpVelocity, currentVelocity.Z);
            }
        }

        UpdateCameraPosition(bodyReference.Pose.Position);
    }

    private void UpdateCameraPosition(System.Numerics.Vector3 bodyPosition)
    {
        _camera.SetPosition(bodyPosition.ToOpenTK() + new Vector3(0, EyeHeight - Height / 2f, 0));
    }
}