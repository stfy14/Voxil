// /Core/InputManager.cs
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Mathematics;

public class InputManager
{
    private KeyboardState _keyboardState;
    private MouseState _mouseState;
    private Vector2 _lastMousePosition;
    private bool _firstMouseMove = true;

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

    public bool IsKeyDown(Keys key) => _keyboardState.IsKeyDown(key);
    public bool IsKeyPressed(Keys key) => _keyboardState.IsKeyPressed(key);

    public Vector2 GetMovementInput()
    {
        Vector2 movement = Vector2.Zero;
        if (IsKeyDown(MoveForward)) movement.Y += 1;
        if (IsKeyDown(MoveBackward)) movement.Y -= 1;
        if (IsKeyDown(MoveLeft)) movement.X -= 1;
        if (IsKeyDown(MoveRight)) movement.X += 1;

        if (movement.LengthSquared > 0)
            movement.Normalize();
        return movement;
    }

    public bool IsJumpPressed() => IsKeyDown(Jump);
    public bool IsCrouchPressed() => IsKeyDown(Crouch);
    public bool IsSprintPressed() => IsKeyDown(Sprint);
    public bool IsExitPressed() => IsKeyDown(Exit);

    public Vector2 GetMouseDelta()
    {
        if (_firstMouseMove)
        {
            _lastMousePosition = new Vector2(_mouseState.X, _mouseState.Y);
            _firstMouseMove = false;
            return Vector2.Zero;
        }

        Vector2 currentPosition = new Vector2(_mouseState.X, _mouseState.Y);
        Vector2 delta = currentPosition - _lastMousePosition;
        _lastMousePosition = currentPosition;
        return delta * MouseSensitivity;
    }

    public bool IsMouseButtonPressed(MouseButton button) => _mouseState.IsButtonPressed(button);
}