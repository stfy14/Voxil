using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Mathematics;

public class InputManager
{
    private KeyboardState _keyboardState;
    private MouseState _mouseState;
    
    // --- НОВЫЕ ПЕРЕМЕННЫЕ ДЛЯ АППАРАТНОЙ МЫШИ ---
    private Vector2 _accumulatedMouseDelta = Vector2.Zero;
    private bool _isCursorGrabbed = true;
    // --------------------------------------------

    public Keys MoveForward { get; set; } = Keys.W;
    public Keys MoveBackward { get; set; } = Keys.S;
    public Keys MoveLeft { get; set; } = Keys.A;
    public Keys MoveRight { get; set; } = Keys.D;
    public Keys Jump { get; set; } = Keys.Space;
    public Keys Crouch { get; set; } = Keys.LeftShift;
    public Keys Sprint { get; set; } = Keys.LeftControl;
    public Keys Exit { get; set; } = Keys.Escape;
    public float MouseSensitivity { get; set; } = 0.1f;

    public void Update(KeyboardState keyboardState, MouseState mouseState)
    {
        _keyboardState = keyboardState;
        _mouseState = mouseState;
    }

    // --- НОВЫЕ МЕТОДЫ ---
    public void SetCursorGrabbed(bool grabbed)
    {
        _isCursorGrabbed = grabbed;
    }

    // Этот метод будет вызываться ИЗ СОБЫТИЯ окна, минуя лаги поллинга!
    public void AddRawMouseDelta(Vector2 delta)
    {
        if (_isCursorGrabbed)
        {
            _accumulatedMouseDelta += delta;
        }
    }

    public void ResetMouseDelta()
    {
        _accumulatedMouseDelta = Vector2.Zero;
    }

    public Vector2 GetMouseDelta()
    {
        // Возвращаем только то, что накопили через OnMouseMove
        Vector2 delta = _accumulatedMouseDelta * MouseSensitivity;
        
        // Обязательно сбрасываем, чтобы движение не "залипало"
        _accumulatedMouseDelta = Vector2.Zero; 
        
        return delta;
    }
    // -------------------

    public bool IsKeyDown(Keys key) => _keyboardState.IsKeyDown(key);
    public bool IsKeyPressed(Keys key) => _keyboardState.IsKeyPressed(key);
    
    public Vector2 GetMovementInput()
    {
        Vector2 movement = Vector2.Zero;
        if (IsKeyDown(MoveForward)) movement.Y += 1;
        if (IsKeyDown(MoveBackward)) movement.Y -= 1;
        if (IsKeyDown(MoveLeft)) movement.X -= 1;
        if (IsKeyDown(MoveRight)) movement.X += 1;
        if (movement.LengthSquared > 0) movement.Normalize();
        return movement;
    }
    
    public bool IsSprintPressed() => IsKeyDown(Sprint);
    public bool IsMouseButtonPressed(MouseButton button) => _mouseState.IsButtonPressed(button);
}