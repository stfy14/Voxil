// /Core/Game.cs
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

    public Game(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        GL.ClearColor(0.1f, 0.3f, 0.5f, 1.0f);
        GL.Enable(EnableCap.DepthTest);
        GL.CullFace(TriangleFace.Back);

        try
        {
            _shader = new Shader("Shaders/shader.vert", "Shaders/shader.frag");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка загрузки шейдеров: {ex.Message}");
            Close();
            return;
        }

        // 1. Создаем физический мир
        _physicsWorld = new PhysicsWorld();

        // 2. ИСПРАВЛЕНИЕ: Безопасная стартовая позиция - на уровне земли + небольшой запас
        var startPosition = new System.Numerics.Vector3(8f, 35f, 8f); // Снизили с 55 до 35

        // 3. Создаем камеру и ИГРОКА
        _camera = new Camera(VectorExtensions.ToOpenTK(startPosition), Size.X / (float)Size.Y);
        _playerController = new PlayerController(_physicsWorld, _camera, startPosition);

        Console.WriteLine($"[Game] PlayerController.BodyHandle = {_playerController.BodyHandle.Value}");

        // 4. Сообщаем физике о BodyHandle игрока
        _physicsWorld.SetPlayerHandle(_playerController.BodyHandle);

        // 5. Создаем WorldManager, передавая ему ссылку на игрока
        _worldManager = new WorldManager(_physicsWorld, _playerController);

        // 6. КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: Принудительно генерируем стартовые чанки ДО первого кадра
        Console.WriteLine("[Game] Форсируем генерацию стартовых чанков...");
        for (int i = 0; i < 100; i++) // Обрабатываем достаточно чанков
        {
            _worldManager.Update(0.016f); // Симулируем кадр для обработки очереди
        }
        Console.WriteLine("[Game] Стартовые чанки сгенерированы.");

        // 7. Создаем менеджер ввода
        _input = new InputManager();

        CursorState = CursorState.Grabbed;
        Console.WriteLine("[Game] Инициализация завершена.");
    }

    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);
        float deltaTime = (float)e.Time;

        _input.Update(KeyboardState, MouseState);
        if (_input.IsExitPressed())
        {
            Close();
            return;
        }

        _playerController.Update(_input, deltaTime);
        _physicsWorld.Update(deltaTime);
        _worldManager.Update(deltaTime);

        if (_input.IsMouseButtonPressed(MouseButton.Left))
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
    }

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);
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
        _worldManager?.Dispose();
        _shader?.Dispose();
        _physicsWorld?.Dispose();
        Console.WriteLine("[Game] Ресурсы освобождены.");
    }
}