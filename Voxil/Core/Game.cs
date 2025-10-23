// /Core/Game.cs - REFACTORED
using BepuPhysics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;

public class Game : GameWindow
{
    private Shader _shader;
    private Camera _camera;
    private InputManager _input;
    private PhysicsWorld _physicsWorld;
    private WorldManager _worldManager;
    private PlayerController _playerController;

    private bool _isInitialized = false;

    public Game(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        GL.ClearColor(0.53f, 0.81f, 0.92f, 1.0f); // Небесно-голубой
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);

        try
        {
            _shader = new Shader("Shaders/shader.vert", "Shaders/shader.frag");
            Console.WriteLine("[Game] Shaders loaded successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Game] ERROR loading shaders: {ex.Message}");
            Close();
            return;
        }

        // === ПРАВИЛЬНАЯ ПОСЛЕДОВАТЕЛЬНОСТЬ ИНИЦИАЛИЗАЦИИ ===

        // 1. Создаём физический мир
        _physicsWorld = new PhysicsWorld();

        // 2. ИСПРАВЛЕНИЕ: Безопасная стартовая позиция (чуть выше уровня генерации земли)
        var startPosition = new System.Numerics.Vector3(8f, 50f, 8f);

        // 3. Создаём камеру
        _camera = new Camera(VectorExtensions.ToOpenTK(startPosition), Size.X / (float)Size.Y);

        // 4. Создаём контроллер игрока
        _playerController = new PlayerController(_physicsWorld, _camera, startPosition);
        Console.WriteLine($"[Game] PlayerController created with BodyHandle: {_playerController.BodyHandle.Value}");

        // 5. Регистрируем игрока в физическом мире
        _physicsWorld.SetPlayerHandle(_playerController.BodyHandle);

        // 6. Создаём менеджер мира (начнётся генерация чанков)
        _worldManager = new WorldManager(_physicsWorld, _playerController);

        // 7. Создаём менеджер ввода
        _input = new InputManager();

        CursorState = CursorState.Grabbed;
        _isInitialized = true;

        Console.WriteLine("[Game] Initialization complete.");
    }

    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);

        if (!_isInitialized) return;

        float deltaTime = (float)e.Time;

        // 1. Обновляем ввод
        _input.Update(KeyboardState, MouseState);

        if (_input.IsExitPressed())
        {
            Close();
            return;
        }

        // 2. Обновляем игрока (движение, камера)
        _playerController.Update(_input, deltaTime);

        // 3. Обновляем мир (генерация, меши, физика чанков)
        // КРИТИЧЕСКИ ВАЖНО: это делается ДО Simulation.Timestep
        _worldManager.Update(deltaTime);

        // 4. Обновляем физическую симуляцию
        _physicsWorld.Update(deltaTime);

        // 5. Обрабатываем разрушение блоков
        if (_input.IsMouseButtonPressed(MouseButton.Left))
        {
            ProcessBlockDestruction();
        }
    }

    private void ProcessBlockDestruction()
    {
        var cameraPosition = _camera.Position.ToSystemNumerics();
        var lookDirection = _camera.Front.ToSystemNumerics();

        var hitHandler = new VoxelHitHandler
        {
            PlayerBodyHandle = _physicsWorld.GetPlayerState().BodyHandle,
            Simulation = _physicsWorld.Simulation
        };

        _physicsWorld.Simulation.RayCast(cameraPosition, lookDirection, 100f, ref hitHandler);

        if (hitHandler.Hit)
        {
            var hitLocation = cameraPosition + lookDirection * hitHandler.T;
            _worldManager.DestroyVoxelAt(hitHandler.Collidable, hitLocation, hitHandler.Normal);
        }
    }

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);

        if (!_isInitialized) return;

        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _worldManager.Render(_shader, _camera.GetViewMatrix(), _camera.GetProjectionMatrix());

        SwapBuffers();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, Size.X, Size.Y);
        _camera?.UpdateAspectRatio(Size.X / (float)Size.Y);
    }

    protected override void OnUnload()
    {
        base.OnUnload();

        Console.WriteLine("[Game] Unloading...");

        _worldManager?.Dispose();
        _physicsWorld?.Dispose();
        _shader?.Dispose();

        Console.WriteLine("[Game] Resources released.");
    }
}