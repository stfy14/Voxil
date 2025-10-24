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

    // --- НОВЫЕ ПОЛЯ ДЛЯ ОТЛАДКИ ---
    private DebugOverlay _debugOverlay;
    private bool _showDebugOverlay = true;
    private float _debugUpdateTimer = 0f;
    private List<string> _debugLines = new List<string>();
    // -----------------------------

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
        // СОЗДАЕМ ГЕНЕРАТОР ЗДЕСЬ, ЧТОБЫ НАЙТИ ТОЧКУ СПАВНА
        var worldGenerator = new PerlinGenerator(12345);

        // 2. ИЩЕМ БЕЗОПАСНУЮ СТАРТОВУЮ ПОЗИЦИЮ
        var spawnXZ = new Vector2i(8, 8);
        var spawnChunkPos = new Vector3i(spawnXZ.X / Chunk.ChunkSize, 0, spawnXZ.Y / Chunk.ChunkSize);
        var spawnChunkVoxels = new Dictionary<Vector3i, MaterialType>();
        worldGenerator.GenerateChunk(spawnChunkPos, spawnChunkVoxels);

        int groundHeight = 0;
        // Ищем сверху вниз первый твердый блок
        for (int y = 100; y > 0; y--)
        {
            if (spawnChunkVoxels.ContainsKey(new Vector3i(spawnXZ.X % Chunk.ChunkSize, y, spawnXZ.Y % Chunk.ChunkSize)))
            {
                groundHeight = y;
                break;
            }
        }

        // Ставим игрока на 3 блока выше найденной земли
        var startPosition = new System.Numerics.Vector3(spawnXZ.X, groundHeight + 15, spawnXZ.Y);
        Console.WriteLine($"[Game] Found surface at Y={groundHeight}. Spawning player at {startPosition}");

        // 3. Создаём камеру с новой позицией
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

        //Debug overlay
        _debugOverlay = new DebugOverlay(Size.X, Size.Y);
        _isInitialized = true;

        Console.WriteLine("[Game] Initialization complete.");
    }

    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);
        if (!_isInitialized) return;
        float deltaTime = (float)e.Time;

        // 1. Ввод
        _input.Update(KeyboardState, MouseState);
        if (_input.IsExitPressed())
        {
            Close();
            return;
        }

        // --- НОВЫЙ КОД ДЛЯ УПРАВЛЕНИЯ ОВЕРЛЕЕМ ---
        if (_input.IsKeyPressed(Keys.F3))
        {
            _showDebugOverlay = !_showDebugOverlay;
        }

        // Обновляем данные для оверлея раз в секунду
        _debugUpdateTimer += deltaTime;
        if (_debugUpdateTimer >= 0.2f)
        {
            _debugUpdateTimer = 0f;
            var averages = PerformanceMonitor.GetAveragesAndReset();
            _debugLines.Clear();
            _debugLines.Add("--- Avg Thread Times (ms/task) ---");
            _debugLines.Add($"Generation: {averages[ThreadType.Generation]:F2}");
            _debugLines.Add($"Meshing:    {averages[ThreadType.Meshing]:F2}");
            _debugLines.Add($"Physics:    {averages[ThreadType.Physics]:F2}");
            _debugLines.Add($"Detachment: {averages[ThreadType.Detachment]:F2}");
        }

        // 2. Логика игрока
        _playerController.Update(_input, deltaTime);

        // 3. Обновление мира (БЕЗ обновления позиций динамических объектов)
        _worldManager.Update(deltaTime);

        // 4. ШАГ ФИЗИЧЕСКОЙ СИМУЛЯЦИИ
        _physicsWorld.Update(deltaTime);

        // 5. --- НОВЫЙ ШАГ ---
        // ОБНОВЛЯЕМ ВИДИМЫЕ ПОЗИЦИИ из нового состояния физики
        _worldManager.ProcessVoxelObjects();

        // 6. Обработка разрушения (если есть)
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

        // ИСПРАВЛЕНИЕ v2.5: Добавлен аргумент _physicsWorld.Simulation.BufferPool
        _physicsWorld.Simulation.RayCast(cameraPosition, lookDirection, 100f, _physicsWorld.Simulation.BufferPool, ref hitHandler);

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

        // --- НОВЫЙ КОД ДЛЯ РЕНДЕРИНГА ОВЕРЛЕЯ ---
        if (_showDebugOverlay)
        {
            _debugOverlay.Render(_debugLines);
        }
        // --------------------------------------

        SwapBuffers();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, Size.X, Size.Y);
        _camera?.UpdateAspectRatio(Size.X / (float)Size.Y);

        // --- ОБНОВЛЯЕМ РАЗМЕР ДЛЯ ОВЕРЛЕЯ ---
        _debugOverlay?.UpdateScreenSize(Size.X, Size.Y);
    }

    protected override void OnUnload()
    {
        base.OnUnload();

        Console.WriteLine("[Game] Unloading...");

        _worldManager?.Dispose();
        _physicsWorld?.Dispose();
        _shader?.Dispose();
        _debugOverlay?.Dispose();

        Console.WriteLine("[Game] Resources released.");
    }
}