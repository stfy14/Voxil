// Game.cs
using OpenTK.Windowing.Desktop;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Mathematics;
using System;

public class Game : GameWindow
{
    private Shader _shader;
    private Chunk _chunk;
    private Player _player;
    private InputManager _input;

    // Единственный объект, отвечающий за всю физику в игре
    private PhysicsWorld _physicsWorld;

    public Game(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        // Настройки OpenGL
        GL.ClearColor(0.1f, 0.3f, 0.5f, 1.0f);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);

        // 1. Инициализируем мир физики
        _physicsWorld = new PhysicsWorld();

        // 2. Загружаем шейдеры
        try
        {
            _shader = new Shader("shader.vert", "shader.frag");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка загрузки шейдеров: {ex.Message}");
            Close();
            return;
        }

        // 3. Создаем чанк и передаем ему ссылку на мир физики
        _chunk = new Chunk(new Vector3i(0, 0, 0), _physicsWorld);

        // 4. Создаем игрока и систему ввода
        Vector3 playerStartPosition = new Vector3(Chunk.ChunkSize / 2f, 15f, Chunk.ChunkSize / 2f + 10f);
        _player = new Player(playerStartPosition, Size.X / (float)Size.Y);
        _input = new InputManager();

        CursorState = CursorState.Grabbed;
        Console.WriteLine("[Game] Инициализация завершена");
        Console.WriteLine("Управление: WASD - движение, Shift - присесть, Ctrl - бежать, Space - прыжок, ESC - выход");
    }

    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);

        float deltaTime = (float)e.Time;

        // Обновляем InputManager
        _input.Update(KeyboardState, MouseState);

        // Проверка выхода
        if (_input.IsExitPressed())
        {
            Close();
            return;
        }

        // Обновляем всю физическую симуляцию
        _physicsWorld.Update(deltaTime);

        // Обновляем игрока (камеру, ввод)
        _player.Update(deltaTime, _input);

        if (_input.IsMouseButtonPressed(MouseButton.Left))
        {
            var cameraPosition = _player.GetCamera().Position.ToSystemNumerics();
            var lookDirection = _player.GetLookDirection().ToSystemNumerics();

            // Измените вызов Raycast, чтобы он принимал 'out hitNormal'
            if (_physicsWorld.Raycast(cameraPosition, lookDirection, 100f, out var hitBody, out var hitLocation, out var hitNormal))
            {
                // Передайте hitNormal в метод разрушения
                _chunk.DestroyVoxelAt(hitBody, hitLocation, hitNormal);
            }
        }

        // Отладочная информация
        if (KeyboardState.IsKeyPressed(Keys.F3))
        {
            Console.WriteLine(_player.ToString());
        }
    }

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);

        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Синхронизируем визуальное состояние с физическим
        _chunk.Update();

        // Рендерим все объекты в чанке, используя матрицы камеры игрока
        _chunk.Render(_shader, _player.GetViewMatrix(), _player.GetProjectionMatrix());

        SwapBuffers();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, Size.X, Size.Y);
        _player?.UpdateCameraAspectRatio(Size.X / (float)Size.Y);
    }

    protected override void OnUnload()
    {
        base.OnUnload();

        // Освобождаем ресурсы в порядке, обратном созданию
        _chunk?.Dispose();
        _shader?.Dispose();
        _physicsWorld?.Dispose();

        Console.WriteLine("[Game] Ресурсы освобождены");
    }
}

/// <summary>
/// Вспомогательный класс с методами-расширениями для конвертации векторов
/// между OpenTK и System.Numerics (используется в BEPUphysics).
/// Его можно вынести в отдельный файл, например, "Utils.cs" или "VectorExtensions.cs".
/// </summary>
public static class VectorExtensions
{
    public static System.Numerics.Vector3 ToSystemNumerics(this OpenTK.Mathematics.Vector3 vec)
    {
        return new System.Numerics.Vector3(vec.X, vec.Y, vec.Z);
    }

    public static OpenTK.Mathematics.Vector3 ToOpenTK(this System.Numerics.Vector3 vec)
    {
        return new OpenTK.Mathematics.Vector3(vec.X, vec.Y, vec.Z);
    }
}