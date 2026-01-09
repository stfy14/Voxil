using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Text;

public class Game : GameWindow
{
    // Основные системы
    private Camera _camera;
    private InputManager _input;
    private PhysicsWorld _physicsWorld;
    private WorldManager _worldManager;
    private PlayerController _playerController;
    private GpuRaycastingRenderer _renderer;
    
    // Вспомогательные системы
    private LineRenderer _lineRenderer;
    private PhysicsDebugDrawer _physicsDebugger;
    private Crosshair _crosshair;
    private TestManager _testManager;

    // Счетчики и флаги
    private int _frameCount = 0; 
    private float _debugUpdateTimer = 0f;
    private bool _isInitialized = false;

    // UI (ImGui)
    private WindowManager _uiManager;
    private SettingsWindow _settingsWindow;
    private MainMenuWindow _mainMenuWindow;
    private DebugStatsWindow _debugStatsWindow;

    public Game(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        GL.ClearColor(0.5f, 0.7f, 0.9f, 1.0f);
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);

        _physicsWorld = new PhysicsWorld();
        _physicsWorld.SetThreadCount(GameSettings.PhysicsThreads);

        var startPosition = new System.Numerics.Vector3(8, 100, 8);
        _camera = new Camera(VectorExtensions.ToOpenTK(startPosition), ClientSize.X / (float)ClientSize.Y);
        _playerController = new PlayerController(_physicsWorld, _camera, startPosition);
        _physicsWorld.SetPlayerHandle(_playerController.BodyHandle);

        _worldManager = new WorldManager(_physicsWorld, _playerController);

        _renderer = new GpuRaycastingRenderer(_worldManager);
        _renderer.Load();
        _renderer.OnResize(ClientSize.X, ClientSize.Y);

        _input = new InputManager();
        _lineRenderer = new LineRenderer();
        _physicsDebugger = new PhysicsDebugDrawer();
        _crosshair = new Crosshair(ClientSize.X, ClientSize.Y);
        _testManager = new TestManager(_worldManager, _camera);

        _worldManager.OnChunkLoaded += (chunk) => _renderer.NotifyChunkLoaded(chunk);
        _worldManager.OnChunkModified += (chunk) => _renderer.NotifyChunkLoaded(chunk);
        _worldManager.OnVoxelFastDestroyed += (pos) => { };
        _worldManager.OnChunkUnloaded += (pos) => _renderer.UnloadChunk(pos);

        _renderer.UploadAllVisibleChunks();

        // --- UI INIT ---
        _uiManager = new WindowManager(this);
        
        _settingsWindow = new SettingsWindow(_worldManager, _renderer);
        _mainMenuWindow = new MainMenuWindow(this, _settingsWindow);
        _debugStatsWindow = new DebugStatsWindow();

        _uiManager.AddWindow(_settingsWindow);
        _uiManager.AddWindow(_mainMenuWindow);
        _uiManager.AddWindow(_debugStatsWindow);

        // Сразу захватываем курсор и сбрасываем дельту
        CursorState = CursorState.Grabbed;
        _input.ResetMouseDelta();

        _isInitialized = true;
        Console.WriteLine("[Game] Initialization Complete.");
    }

    protected override void OnUpdateFrame(FrameEventArgs e)
{
    base.OnUpdateFrame(e);
    if (!_isInitialized) return;

    // =========================================================================
    // === НАЧАЛО ИЗМЕНЕНИЙ: ДВУХКАДРОВАЯ ПЕРЕЗАГРУЗКА ========================
    // =========================================================================
    // Проверяем, не была ли в прошлом кадре запрошена перезагрузка ресурсов.
    // Это вторая фаза нашего плана, которая выполняется в следующем кадре,
    // чтобы дать драйверу время освободить память.
    if (_renderer.IsReallocationPending())
    {
        // 1. Физически пересоздаем буферы на GPU с новым размером.
        _renderer.PerformReallocation();

        // 2. Теперь, когда рендерер готов, перезапускаем WorldManager,
        // чтобы он начал загружать чанки в новые, чистые буферы.
        _worldManager.ReloadWorld();
    }
    // =========================================================================
    // === КОНЕЦ ИЗМЕНЕНИЙ =====================================================
    // =========================================================================

    float deltaTime = (float)e.Time;

    // 1. Обновление UI
    _uiManager.Update(this, deltaTime);

    // 2. Логика состояния курсора (ИСПРАВЛЕНА)
    bool isMenuOpen = _mainMenuWindow.IsVisible || _settingsWindow.IsVisible;

    if (isMenuOpen)
    {
        // Если меню открыто, курсор должен быть обычным
        if (CursorState != CursorState.Normal)
        {
            CursorState = CursorState.Normal;
        }
    }
    else
    {
        // Если меню закрыто, курсор должен быть захвачен
        if (CursorState != CursorState.Grabbed)
        {
            CursorState = CursorState.Grabbed;
            // Сбрасываем дельту ТОЛЬКО ОДИН РАЗ в момент захвата,
            // чтобы камеру не дернуло.
            _input.ResetMouseDelta(); 
        }
    }

    // 3. Ввод
    _input.Update(KeyboardState, MouseState);

    // Escape: Тоглим меню
    if (_input.IsKeyPressed(Keys.Escape))
    {
        if (_settingsWindow.IsVisible) _settingsWindow.Toggle();
        else _mainMenuWindow.Toggle();
    }

    // 4. Игровой процесс (только если меню закрыто)
    if (!isMenuOpen)
    {
        if (_input.IsKeyPressed(Keys.F3))
        {
            PerformanceMonitor.IsEnabled = !PerformanceMonitor.IsEnabled;
            _debugStatsWindow.Toggle();
        }

        if (_input.IsKeyPressed(Keys.F)) 
        {
            _playerController.ToggleFly();
        }

        _testManager.Update(deltaTime, _input);
        _playerController.Update(_input, deltaTime); // Камера двигается здесь
        
        if (_input.IsMouseButtonPressed(MouseButton.Left)) 
        {
            ProcessBlockDestruction();
        }
    }

    // 5. Обновление мира
    _worldManager.Update(deltaTime);
    _physicsWorld.Update(deltaTime);
    _renderer.Update(deltaTime);

    // 6. Дебаг текст
    UpdateDebugStats(deltaTime);
}

    private void UpdateDebugStats(float deltaTime)
    {
        if (!_debugStatsWindow.IsVisible || !PerformanceMonitor.IsEnabled) return;

        _debugUpdateTimer += deltaTime;
        
        if (_debugUpdateTimer >= 0.5f)
        {
            float avgFps = (float)_frameCount / _debugUpdateTimer;
            var averages = PerformanceMonitor.GetDataAndReset(_debugUpdateTimer);
            
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"FPS: {avgFps:F0}");
            sb.AppendLine($"Pos: {_camera.Position.X:F0} {_camera.Position.Y:F0} {_camera.Position.Z:F0}");
            sb.AppendLine("------------------");
            sb.AppendLine("--- Perf ---");
            if (averages != null)
            {
                foreach(var kvp in averages) 
                    sb.AppendLine($"{kvp.Key}: {kvp.Value}");
            }

            sb.AppendLine("------------------");
            sb.AppendLine("--- Queues ---");
            sb.AppendLine($"Gen Pending: {_worldManager.GeneratorPendingCount}");
            sb.AppendLine($"Gen Results: {_worldManager.GeneratorResultsCount}");
            sb.AppendLine($"Phys Urgent: {_worldManager.PhysicsUrgentCount}");
            sb.AppendLine($"Phys Pending:{_worldManager.PhysicsPendingCount}");
            sb.AppendLine($"Phys Results:{_worldManager.PhysicsResultsCount}");
            sb.AppendLine($"World Chunks: {_worldManager.LoadedChunkCount}");
            sb.AppendLine($"Chunks InPrg: {_worldManager.ChunksInProgressCount}");
            sb.AppendLine($"Chunks UnloadQ:{_worldManager.UnloadQueueCount}");

            _debugStatsWindow.UpdateText(sb.ToString());
            
            _debugUpdateTimer = 0f;
            _frameCount = 0;
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
        
        _frameCount++; 

        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _renderer.Render(_camera);
        
        // Рисуем прицел только если меню закрыто
        if (!_mainMenuWindow.IsVisible && !_settingsWindow.IsVisible)
        {
            _crosshair.Render();
        }

        // Рендер UI
        _uiManager.Render();

        SwapBuffers();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);
        _camera?.UpdateAspectRatio(ClientSize.X / (float)ClientSize.Y);
        _renderer?.OnResize(ClientSize.X, ClientSize.Y);
        _crosshair?.UpdateSize(ClientSize.X, ClientSize.Y);
        _uiManager?.Resize(ClientSize.X, ClientSize.Y);
    }

    protected override void OnUnload()
    {
        base.OnUnload();
        _renderer?.Dispose();
        _worldManager?.Dispose();
        _physicsWorld?.Dispose();
        _uiManager?.Dispose();
        _lineRenderer?.Dispose();
        _crosshair?.Dispose();
        Console.WriteLine("[Game] Unloaded.");
    }
}