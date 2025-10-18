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

    // НОВОЕ: Целевая горизонтальная скорость, которую мы передадим в физику
    public System.Numerics.Vector2 DesiredHorizontalVelocity { get; private set; }

    private readonly PhysicsWorld _physicsWorld;
    private readonly Camera _camera;
    private float _coyoteTime = 0f;
    private const float CoyoteTimeDuration = 0.1f;

    public PlayerController(PhysicsWorld physicsWorld, Camera camera, System.Numerics.Vector3 startPosition)
    {
        _physicsWorld = physicsWorld;
        _camera = camera;

        float radius = Width / 2f;
        // КЛЮЧЕВОЕ ИЗМЕНЕНИЕ: Используем капсулу вместо цилиндра!
        var shape = new Capsule(radius, Height - 2 * radius);
        var shapeIndex = physicsWorld.Simulation.Shapes.Add(shape);

        var bodyDescription = BodyDescription.CreateDynamic(
            new RigidPose(startPosition),
            new BodyInertia { InverseMass = 1f / 70f }, // Масса 70 кг
            new CollidableDescription(shapeIndex, 0.1f),
            new BodyActivityDescription(0f));

        // Блокируем вращение
        bodyDescription.LocalInertia.InverseInertiaTensor = default;

        BodyHandle = physicsWorld.Simulation.Bodies.Add(bodyDescription);
        Console.WriteLine($"[PlayerController] Создан с BodyHandle: {BodyHandle.Value} (форма: Capsule)");
    }

    public void Update(InputManager input, float deltaTime)
    {
        var bodyReference = _physicsWorld.Simulation.Bodies.GetBodyReference(BodyHandle);
        var bodyPosition = bodyReference.Pose.Position;

        // 1. Вращение камеры (остается без изменений)
        Vector2 mouseDelta = input.GetMouseDelta();
        _camera.Rotate(mouseDelta.X, mouseDelta.Y);

        // 2. Проверка опоры под ногами (остается почти без изменений)
        CheckGrounded(bodyPosition);

        // 3. Определяем ЖЕЛАЕМУЮ горизонтальную скорость
        Vector2 movementInput = input.GetMovementInput();
        var desiredVelocity = System.Numerics.Vector2.Zero;
        if (movementInput.LengthSquared > 0.01f)
        {
            var forward = new System.Numerics.Vector3(_camera.Front.X, 0, _camera.Front.Z);
            var right = new System.Numerics.Vector3(_camera.Right.X, 0, _camera.Right.Z);

            // Нормализуем, чтобы избежать ускорения по диагонали
            var moveDirection = System.Numerics.Vector3.Normalize(forward * movementInput.Y + right * movementInput.X);

            float speed = input.IsSprintPressed() ? SprintSpeed : WalkSpeed;
            desiredVelocity = new System.Numerics.Vector2(moveDirection.X, moveDirection.Z) * speed;
        }
        DesiredHorizontalVelocity = desiredVelocity;

        // 4. Обработка прыжка
        if (IsOnGround)
        {
            _coyoteTime = CoyoteTimeDuration;
        }
        else
        {
            _coyoteTime = Math.Max(0, _coyoteTime - deltaTime);
        }

        if (input.IsJumpPressed() && _coyoteTime > 0)
        {
            // Применяем ОДНОРАЗОВЫЙ импульс. Дальше гравитация сделает свое дело.
            var currentVelocity = bodyReference.Velocity.Linear;
            bodyReference.Velocity.Linear = new System.Numerics.Vector3(currentVelocity.X, JumpVelocity, currentVelocity.Z);
            _coyoteTime = 0; // Предотвращаем двойной прыжок
        }

        // 5. Обновление позиции камеры
        UpdateCameraPosition(bodyPosition);
    }

    private void CheckGrounded(System.Numerics.Vector3 bodyPosition)
    {
        // Для капсулы луч нужно пускать чуть ниже
        float rayLength = (Height / 2f) + 0.15f;
        var down = -System.Numerics.Vector3.UnitY;

        // Проверяем землю с помощью одного луча из центра. 
        // Для капсулы этого обычно достаточно, но можно вернуть и боковые лучи при необходимости.
        IsOnGround = _physicsWorld.Raycast(bodyPosition, down, rayLength, out _, out _, out _, BodyHandle);
    }

    private void UpdateCameraPosition(System.Numerics.Vector3 bodyPosition)
    {
        _camera.SetPosition(bodyPosition.ToOpenTK() + new Vector3(0, EyeHeight - Height / 2f, 0));
    }
}