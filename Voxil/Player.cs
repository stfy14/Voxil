// Player.cs
using OpenTK.Mathematics;
using System;

public class Player
{
    // Физические параметры
    public Vector3 Position { get; private set; }
    public Vector3 Velocity { get; private set; }

    // Размеры игрока (для коллизий)
    public const float PlayerHeight = 1.8f;
    public const float PlayerWidth = 0.6f;
    public const float EyeHeight = 1.62f; // Высота глаз от ног

    // Параметры движения
    public float WalkSpeed { get; set; } = 4.317f; // Как в Minecraft
    public float SprintSpeed { get; set; } = 5.612f;
    public float CrouchSpeed { get; set; } = 1.3f;
    public float JumpVelocity { get; set; } = 8.0f;
    public float Gravity { get; set; } = 20.0f;

    // Состояние
    public bool IsOnGround { get; private set; }
    public bool IsSprinting { get; private set; }
    public bool IsCrouching { get; private set; }

    // Камера
    private readonly Camera _camera;

    public Player(Vector3 startPosition, float aspectRatio)
    {
        Position = startPosition;
        Velocity = Vector3.Zero;

        // Камера находится на уровне глаз игрока
        Vector3 cameraPosition = Position + new Vector3(0, EyeHeight, 0);
        _camera = new Camera(cameraPosition, cameraPosition + Vector3.UnitZ, aspectRatio);
    }

    public void Update(float deltaTime, InputManager input)
    {
        // Обновляем состояния
        IsSprinting = input.IsSprintPressed() && !IsCrouching;
        IsCrouching = input.IsCrouchPressed();

        // Получаем ввод движения
        Vector2 movementInput = input.GetMovementInput();

        // Вычисляем скорость в зависимости от состояния
        float currentSpeed = WalkSpeed;
        if (IsSprinting) currentSpeed = SprintSpeed;
        if (IsCrouching) currentSpeed = CrouchSpeed;

        // Движение относительно направления камеры
        Vector3 moveDirection = Vector3.Zero;
        if (movementInput.LengthSquared > 0)
        {
            Vector3 forward = _camera.Front;
            forward.Y = 0; // Игнорируем Y, чтобы не летать вверх при взгляде вверх
            forward = Vector3.Normalize(forward);

            Vector3 right = _camera.Right;
            right.Y = 0;
            right = Vector3.Normalize(right);

            moveDirection = (forward * movementInput.Y + right * movementInput.X);
            moveDirection = Vector3.Normalize(moveDirection);
        }

        // Применяем горизонтальную скорость
        Vector3 horizontalVelocity = moveDirection * currentSpeed;

        // Прыжок
        if (input.IsJumpPressed() && IsOnGround && !IsCrouching)
        {
            Velocity = new Vector3(Velocity.X, JumpVelocity, Velocity.Z);
            IsOnGround = false;
        }

        // Применяем гравитацию
        if (!IsOnGround)
        {
            Velocity -= new Vector3(0, Gravity * deltaTime, 0);
        }

        // Объединяем горизонтальную и вертикальную составляющие
        Vector3 finalVelocity = new Vector3(horizontalVelocity.X, Velocity.Y, horizontalVelocity.Z);

        // Обновляем позицию
        Position += finalVelocity * deltaTime;

        // Простая проверка земли (временно, пока нет коллизий)
        // TODO: Заменить на реальную проверку коллизий с блоками
        if (Position.Y <= 0)
        {
            Position = new Vector3(Position.X, 0, Position.Z);
            Velocity = new Vector3(Velocity.X, 0, Velocity.Z);
            IsOnGround = true;
        }
        else
        {
            IsOnGround = false;
        }

        // Обновляем камеру
        UpdateCamera(input);
    }

    private void UpdateCamera(InputManager input)
    {
        // Получаем дельту мыши
        Vector2 mouseDelta = input.GetMouseDelta();

        // Поворачиваем камеру
        _camera.Rotate(mouseDelta.X, mouseDelta.Y);

        // Синхронизируем позицию камеры с позицией глаз игрока
        float eyeHeightOffset = IsCrouching ? EyeHeight * 0.7f : EyeHeight;
        Vector3 cameraPosition = Position + new Vector3(0, eyeHeightOffset, 0);
        _camera.SetPosition(cameraPosition);
    }

    public void Teleport(Vector3 position)
    {
        Position = position;
        Velocity = Vector3.Zero;
    }

    // Методы для доступа к камере
    public Matrix4 GetViewMatrix() => _camera.GetViewMatrix();
    public Matrix4 GetProjectionMatrix() => _camera.GetProjectionMatrix();
    public void UpdateCameraAspectRatio(float aspectRatio) => _camera.UpdateAspectRatio(aspectRatio);
    public Vector3 GetLookDirection() => _camera.Front;
    public Camera GetCamera() => _camera;

    // Для отладки
    public override string ToString()
    {
        return $"Player [Pos: {Position:F2}, Vel: {Velocity:F2}, OnGround: {IsOnGround}]";
    }
}