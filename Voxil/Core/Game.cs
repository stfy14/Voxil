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
        GL.Enable(EnableCap.CullFace);
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

        // ИЗМЕНЕНИЕ: ПОРЯДОК ОПЕРАЦИЙ

        // 1. Создаем физический мир.
        _physicsWorld = new PhysicsWorld();

        // 2. Создаем менеджер мира. В этот момент его конструктор
        //    СРАЗУ ЖЕ генерирует стартовую землю.
        _worldManager = new WorldManager(_physicsWorld);

        // 3. Определяем безопасную стартовую позицию НАД сгенерированной землей.
        //    Если земля на Y=50, ставим игрока на Y=55.
        var startPosition = new System.Numerics.Vector3(8f, 55f, 8f);

        // 4. Создаем камеру и игрока в мире, где УЖЕ ЕСТЬ земля.
        _camera = new Camera(VectorExtensions.ToOpenTK(startPosition), Size.X / (float)Size.Y);
        _playerController = new PlayerController(_physicsWorld, _camera, startPosition);

        // 5. Создаем менеджер ввода.
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

        _playerController.Update(_input);
        _physicsWorld.Update(deltaTime);
        _worldManager.Update();

        if (_input.IsMouseButtonPressed(MouseButton.Left))
        {
            var cameraPosition = _camera.Position.ToSystemNumerics();
            var lookDirection = _camera.Front.ToSystemNumerics();

            var hitHandler = new VoxelHitHandler
            {
                PlayerBodyHandle = _playerController.BodyHandle,
                Simulation = _physicsWorld.Simulation
            };

            _physicsWorld.Simulation.RayCast(cameraPosition, lookDirection, 100f, ref hitHandler);

            if (hitHandler.Hit)
            {
                Console.WriteLine($"[Raycast] Попадание в объект.");
                // ИСПРАВЛЕНО: hitLocation и hitNormal уже являются System.Numerics.Vector3,
                // поэтому их можно передавать напрямую в исправленный метод DestroyVoxelAt.
                var hitLocation = cameraPosition + lookDirection * hitHandler.T;
                _worldManager.DestroyVoxelAt(hitHandler.Collidable, hitLocation, hitHandler.Normal);
            }
            else
            {
                Console.WriteLine("[Raycast] Промах.");
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