// --- START OF FILE Game.cs ---
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Text;

public class Game : GameWindow
{
    // --- Ядро ---
    private Camera _camera;
    private InputManager _input;
    private PhysicsWorld _physicsWorld;
    private WorldManager _worldManager;
    private EntityManager _entityManager;
    private PlayerController _playerController;

    // --- Сцены ---
    private SceneManager _sceneManager;
    private GameScene _gameScene;
    private EditorScene _editorScene;

    // --- Рендер ---
    private GpuRaycastingRenderer _renderer;
    private LineRenderer _lineRenderer;
    private PhysicsDebugDrawer _physicsDebugger;
    private Crosshair _crosshair;

    // --- Игра ---
    private Player _player;
    private TestManager _testManager;

    // --- UI ---
    private WindowManager _uiManager;
    private InventoryWindow _invWindow;
    private SettingsWindow _settingsWindow;
    private TimeSettingsWindow _timeSettingsWindow;
    private VisualDebugWindow _visualDebugWindow;
    private DebugStatsWindow _debugStatsWindow;
    private VoxelInspectorWindow _voxelInspectorWindow;
    private MainToolbarWindow _toolbarWindow;
    private EditorUIWindow _editorUIWindow;
    private bool _isUIMode = false;

    // --- Служебное ---
    private bool _isInitialized = false;
    private int _frameCount = 0;
    private float _debugUpdateTimer = 0f;
    private int _reallocationDelayFrames = 0;

    public Game(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings) { }

    protected override void OnLoad()
    {
        base.OnLoad();

        GL.ClearColor(0.5f, 0.7f, 0.9f, 1.0f);
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);

        InitCore();
        InitServices();
        InitRenderer();
        InitGame();
        InitScenes();
        InitUI();

        _input.ResetMouseDelta();
        _isInitialized = true;
        CursorState = CursorState.Grabbed;
    }

    // =========================================================
    // ИНИЦИАЛИЗАЦИЯ
    // =========================================================

    private void InitCore()
    {
        _physicsWorld = new PhysicsWorld();
        _physicsWorld.SetThreadCount(GameSettings.PhysicsThreads);

        var startPosition = new System.Numerics.Vector3(8, 100, 8);
        _camera = new Camera(VectorExtensions.ToOpenTK(startPosition), ClientSize.X / (float)ClientSize.Y);

        _playerController = new PlayerController(_physicsWorld, _camera, startPosition);
        _physicsWorld.SetPlayerHandle(_playerController.BodyHandle);

        _entityManager = new EntityManager();
        ServiceLocator.Register<EntityManager>(_entityManager);

        _worldManager = new WorldManager(_physicsWorld, _playerController);
        ServiceLocator.Register<IWorldService>(_worldManager);
    }

    private void InitServices()
    {
        var voxelEditService   = new VoxelEditService(_worldManager);
        var voxelObjectService = new VoxelObjectService(_worldManager);

        ServiceLocator.Register<IVoxelEditService>(voxelEditService);
        ServiceLocator.Register<IVoxelObjectService>(voxelObjectService);
    }

    private void InitRenderer()
    {
        _renderer = new GpuRaycastingRenderer(_worldManager);
        _renderer.Load();
        _renderer.OnResize(ClientSize.X, ClientSize.Y);

        PerformanceMonitor.MemoryInfoProvider = () => _renderer.GetMemoryDebugInfo();

        _worldManager.OnChunkLoaded        += chunk             => _renderer.NotifyChunkLoaded(chunk);
        _worldManager.OnChunkModified      += chunk             => _renderer.NotifyChunkLoaded(chunk);
        _worldManager.OnChunkUnloaded      += pos               => _renderer.UnloadChunk(pos);
        _worldManager.OnVoxelEdited        += (chunk, pos, mat) => _renderer.NotifyVoxelEdited(chunk, pos, mat);
        _worldManager.OnVoxelFastDestroyed += pos               => { };

        _renderer.UploadAllVisibleChunks();

        _lineRenderer    = new LineRenderer();
        _physicsDebugger = new PhysicsDebugDrawer();
        _crosshair       = new Crosshair(ClientSize.X, ClientSize.Y);
    }

    private void InitGame()
    {
        _input       = new InputManager();
        _player      = new Player(_playerController, _camera, _worldManager);
        _testManager = new TestManager(_camera);
    }

    private void InitScenes()
    {
        _sceneManager = new SceneManager();
        ServiceLocator.Register<SceneManager>(_sceneManager);

        _gameScene = new GameScene(
            _worldManager, _physicsWorld, _renderer,
            _player, _entityManager, _camera, _input,
            _lineRenderer, _physicsDebugger, _crosshair, _testManager);
        
        _editorScene = new EditorScene(_renderer, (float)ClientSize.X / ClientSize.Y, ClientSize.X, ClientSize.Y);
        _editorScene.OnExitRequested += SwitchToGame;

        _sceneManager.Register(_gameScene);
        _sceneManager.Register(_editorScene);

        _sceneManager.SwitchTo<GameScene>();
    }

    private void InitUI()
    {
        _uiManager = new WindowManager(this);

        _invWindow = new InventoryWindow(_player);
        _settingsWindow       = new SettingsWindow(_worldManager, _renderer);
        _timeSettingsWindow   = new TimeSettingsWindow();
        _visualDebugWindow    = new VisualDebugWindow();
        _debugStatsWindow     = new DebugStatsWindow();
        _voxelInspectorWindow = new VoxelInspectorWindow(_worldManager, _camera);
        _editorUIWindow       = new EditorUIWindow(_editorScene, SwitchToGame);

        _toolbarWindow = new MainToolbarWindow(this);
        _toolbarWindow.RegisterMenuItem("Game Settings",      _settingsWindow);
        _toolbarWindow.RegisterMenuItem("Time & Environment", _timeSettingsWindow);
        _toolbarWindow.RegisterMenuItem("Visual Debug",       _visualDebugWindow);
        _toolbarWindow.RegisterMenuItem("Performance Stats",  _debugStatsWindow);
        _toolbarWindow.RegisterMenuItem("Voxel Inspector",    _voxelInspectorWindow);
        _toolbarWindow.RegisterSceneSwitch("Model Editor",    SwitchToEditor);
        TextInput += e => _uiManager.PressChar((char)e.Unicode);

        _uiManager.AddWindow(_invWindow);
        _uiManager.AddWindow(_settingsWindow);
        _uiManager.AddWindow(_timeSettingsWindow);
        _uiManager.AddWindow(_visualDebugWindow);
        _uiManager.AddWindow(_debugStatsWindow);
        _uiManager.AddWindow(_voxelInspectorWindow);
        _uiManager.AddWindow(_toolbarWindow);
        _uiManager.AddWindow(_editorUIWindow);
    }

    // =========================================================
    // ПЕРЕКЛЮЧЕНИЕ СЦЕН
    // =========================================================

    private void SwitchToEditor()
    {
        _editorUIWindow.IsVisible = true;
        _invWindow.IsVisible     = false; 
        _isUIMode                = false;
        _toolbarWindow.IsVisible = false;
        CursorState              = CursorState.Normal;
        _input.SetCursorGrabbed(false);
        _sceneManager.SwitchTo<EditorScene>();
    }

    private void SwitchToGame()
    {
        _editorUIWindow.IsVisible = false;
        _invWindow.IsVisible = true; 
        CursorState = CursorState.Grabbed;
        _input.SetCursorGrabbed(true);
        _input.ResetMouseDelta();
        _sceneManager.SwitchTo<GameScene>();
    }

    // =========================================================
    // UPDATE
    // =========================================================

    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);
        if (!_isInitialized) return;

        float deltaTime = (float)e.Time;
        bool inEditor = _sceneManager.Current is EditorScene;

        _input.Update(KeyboardState, MouseState);

        // 1. Сообщаем UI, должен ли он перехватывать мышь
        bool uiTakesInput = inEditor || _isUIMode;
        _uiManager.Update(this, deltaTime, uiTakesInput);

        if (inEditor)
        {
            _sceneManager.Update(deltaTime, _input);
            UpdateDebugStats(deltaTime);
            return;
        }

        // --- Игровая сцена ---
        UpdateTime(deltaTime);
        if (HandleReallocation()) return;
        UpdateCursorMode();

        if (!_isUIMode)
        {
            _sceneManager.Update(deltaTime, _input);
        }
        else
        {
            // 2. ИСПРАВЛЕНИЕ ГЕНЕРАЦИИ: 
            // Даже если игра на "паузе" в меню, мы разрешаем менеджеру мира 
            // забирать сгенерированные чанки, а рендеру - отправлять их в видеокарту!
            // Физика при этом стоит на паузе, так как она внутри _sceneManager.Update.
            _worldManager.Update(deltaTime);
            _renderer.UpdateChunkData(deltaTime);
        }

        UpdateDebugStats(deltaTime);
    }

    private void UpdateTime(float deltaTime)
    {
        if (GameSettings.EnableDynamicTime)
            GameSettings.TotalTimeHours += (deltaTime * GameSettings.TimeScale) / 3600.0;
    }

    private bool HandleReallocation()
    {
        if (!_renderer.IsReallocationPending()) return false;

        _reallocationDelayFrames++;
        if (_reallocationDelayFrames >= 3)
        {
            _renderer.PerformReallocation();
            _worldManager.ReloadWorld();
            _renderer.ReloadShader();
            _reallocationDelayFrames = 0;
            GC.Collect();
        }
        return true;
    }

    private void UpdateCursorMode()
    {
        if (_input.IsKeyPressed(Keys.Escape))
        {
            _isUIMode                = !_isUIMode;
            _toolbarWindow.IsVisible = _isUIMode;
        }

        if (!_toolbarWindow.IsVisible && _isUIMode) _isUIMode = false;

        _input.SetCursorGrabbed(!_isUIMode);

        if (_isUIMode)
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
    }

    private void UpdateDebugStats(float deltaTime)
    {
        if (!_debugStatsWindow.IsVisible || !PerformanceMonitor.IsEnabled) return;

        _debugUpdateTimer += deltaTime;
        _frameCount++;

        if (_debugUpdateTimer < 0.5f) return;

        float avgFps = _frameCount / _debugUpdateTimer;
        var averages = PerformanceMonitor.GetDataAndReset(_debugUpdateTimer);

        var sb = new StringBuilder();
        sb.AppendLine($"FPS: {avgFps:F0}");
        sb.AppendLine($"Pos: {_camera.Position.X:F0} {_camera.Position.Y:F0} {_camera.Position.Z:F0}");
        sb.AppendLine("------------------");
        if (averages != null) foreach (var kvp in averages) sb.AppendLine($"{kvp.Key}: {kvp.Value}");
        sb.AppendLine("------------------");
        sb.AppendLine($"Chunks: {_worldManager.LoadedChunkCount} (Q:{_worldManager.GeneratorPendingCount})");

        _debugStatsWindow.UpdateText(sb.ToString());
        _debugUpdateTimer = 0f;
        _frameCount       = 0;
    }

    // =========================================================
    // RENDER
    // =========================================================

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);
        if (!_isInitialized) return;

        bool inEditor = _sceneManager.Current is EditorScene;

        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (!inEditor)
        {
            UpdateViewModel();
            if (!_renderer.IsReallocationPending())
                _renderer.UpdateDynamicObjectsAndGrid();
        }

        _sceneManager.Render();
        _uiManager.Render();
        SwapBuffers();
    }

    private void UpdateViewModel()
    {
        var viewModel = _player.GetViewModel();
        if (viewModel == null) { _renderer.SetViewModel(null); return; }

        Vector3 handOffset = new Vector3(0.4f, -0.4f, -0.8f);
        Vector3 bobbing    = Vector3.Zero;

        if (_input.GetMovementInput().LengthSquared > 0.1f)
        {
            float time = GameSettings.TimeOfDay * 20.0f;
            bobbing.Y  = (float)Math.Sin(time) * 0.015f;
            bobbing.X  = (float)Math.Cos(time * 0.5f) * 0.01f;
        }

        Quaternion itemTilt = Quaternion.FromEulerAngles(
            MathHelper.DegreesToRadians(10),
            MathHelper.DegreesToRadians(-15),
            MathHelper.DegreesToRadians(10));

        MathUtils.CalculateViewModelTransform(
            _camera.GetViewMatrix(), handOffset, itemTilt, bobbing,
            out Vector3 finalPos, out Quaternion finalRot);

        viewModel.ForceSetTransform(finalPos, finalRot);
        _renderer.SetViewModel(viewModel);
    }

    // =========================================================
    // СОБЫТИЯ ОКНА
    // =========================================================

    protected override void OnMouseMove(MouseMoveEventArgs e)
    {
        if (_input == null) return;
    
        if (_sceneManager.Current is EditorScene)
            _input.AddRawMouseDeltaUnconditional(e.Delta);
        else
            _input.AddRawMouseDelta(e.Delta);
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        if (!_isInitialized) return;

        _sceneManager.OnResize(e.Width, e.Height);
        _uiManager.Resize(e.Width, e.Height);
    }

    protected override void OnUnload()
    {
        base.OnUnload();
        _entityManager.Clear();
        EventBus.Clear();
        ServiceLocator.Clear();
    }
}