using BepuPhysics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Text;

public class Game : GameWindow
{
    private Camera _camera;
    private InputManager _input;
    private PhysicsWorld _physicsWorld;
    private WorldManager _worldManager;
    private PlayerController _playerController;
    private GpuRaycastingRenderer _renderer;
    
    private LineRenderer _lineRenderer;
    private PhysicsDebugDrawer _physicsDebugger;
    private Crosshair _crosshair;
    private TestManager _testManager;

    private int _frameCount = 0; 
    private float _debugUpdateTimer = 0f;
    private bool _isInitialized = false;

    private WindowManager _uiManager;
    private SettingsWindow _settingsWindow;
    private MainMenuWindow _mainMenuWindow;
    private DebugStatsWindow _debugStatsWindow;
    
    private int _reallocationDelayFrames = 0;

    public Game(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        //var tester = new VoxelSystemTester();
        //tester.RunStressTest();
        
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
        PerformanceMonitor.MemoryInfoProvider = () => _renderer.GetMemoryDebugInfo();
        
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

        // UI INIT
        _uiManager = new WindowManager(this);
        _settingsWindow = new SettingsWindow(_worldManager, _renderer);
        _mainMenuWindow = new MainMenuWindow(this, _settingsWindow);
        _debugStatsWindow = new DebugStatsWindow();

        _uiManager.AddWindow(_settingsWindow);
        _uiManager.AddWindow(_mainMenuWindow);
        _uiManager.AddWindow(_debugStatsWindow);

        CursorState = CursorState.Grabbed;
        _input.ResetMouseDelta();

        _isInitialized = true;
        Console.WriteLine("[Game] Initialization Complete.");
    }

    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);
        if (!_isInitialized) return;

        float deltaTime = (float)e.Time;

        // 1. СНАЧАЛА UI (чтобы ImGui.NewFrame всегда вызывался)
        _uiManager.Update(this, deltaTime);

        // 2. ЛОГИКА ПЕРЕЗАГРУЗКИ ПАМЯТИ
        if (_renderer.IsReallocationPending())
        {
            _reallocationDelayFrames++;
            if (_reallocationDelayFrames >= 3)
            {
                _renderer.PerformReallocation();
                _worldManager.ReloadWorld();
                _renderer.ReloadShader();
                _reallocationDelayFrames = 0;
                Console.WriteLine("[Game] Reallocation completed.");
                GC.Collect();
            }
            else
            {
                return; // Ждем драйвер, пропускаем остальную логику
            }
        }

        // 3. Управление курсором
        bool isMenuOpen = _mainMenuWindow.IsVisible || _settingsWindow.IsVisible;
        if (isMenuOpen)
        {
            if (CursorState != CursorState.Normal) CursorState = CursorState.Normal;
        }
        else
        {
            if (CursorState != CursorState.Grabbed)
            {
                CursorState = CursorState.Grabbed;
                _input.ResetMouseDelta(); 
            }
        }

        // 4. Ввод
        _input.Update(KeyboardState, MouseState);
        if (_input.IsKeyPressed(Keys.Escape))
        {
            if (_settingsWindow.IsVisible) _settingsWindow.Toggle();
            else _mainMenuWindow.Toggle();
        }

        // 5. Геймплей
        if (!isMenuOpen)
        {
            if (_input.IsKeyPressed(Keys.F3))
            {
                PerformanceMonitor.IsEnabled = !PerformanceMonitor.IsEnabled;
                _debugStatsWindow.Toggle();
            }
            if (_input.IsKeyPressed(Keys.F)) _playerController.ToggleFly();

            _testManager.Update(deltaTime, _input);
            _playerController.Update(_input, deltaTime);
            
            if (_input.IsMouseButtonPressed(MouseButton.Left)) ProcessBlockDestruction();
        }

        // 6. Обновление мира
        if (!_renderer.IsReallocationPending())
        {
            _worldManager.Update(deltaTime);
            _physicsWorld.Update(deltaTime);
            _renderer.Update(deltaTime);
        }

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
            if (averages != null) foreach(var kvp in averages) sb.AppendLine($"{kvp.Key}: {kvp.Value}");
            sb.AppendLine("------------------");
            sb.AppendLine($"Chunks: {_worldManager.LoadedChunkCount} (Q:{_worldManager.GeneratorPendingCount})");
            
            _debugStatsWindow.UpdateText(sb.ToString());
            _debugUpdateTimer = 0f;
            _frameCount = 0;
        }
    }

    private void ProcessBlockDestruction()
    {
        var pos = _camera.Position.ToSystemNumerics();
        var dir = _camera.Front.ToSystemNumerics();
        var hit = new VoxelHitHandler { PlayerBodyHandle = _physicsWorld.GetPlayerState().BodyHandle, Simulation = _physicsWorld.Simulation };
        _physicsWorld.Simulation.RayCast(pos, dir, 100f, _physicsWorld.Simulation.BufferPool, ref hit);
        if (hit.Hit) _worldManager.DestroyVoxelAt(hit.Collidable, pos + dir * hit.T, hit.Normal);
    }

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);
        if (!_isInitialized) return;
        _frameCount++; 
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _renderer.Render(_camera);
        // Рисуем дебаг коллизий (поверх всего)
        _physicsDebugger.Draw(_physicsWorld, _lineRenderer, _camera);
        _lineRenderer.Render(_camera); // Фактическая отправка линий на GPU
        if (!_mainMenuWindow.IsVisible && !_settingsWindow.IsVisible) _crosshair.Render();
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