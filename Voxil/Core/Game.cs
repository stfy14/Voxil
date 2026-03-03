// --- START OF FILE Game.cs ---
using BepuPhysics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Text;
using System.Collections.Generic;

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

    private Player _player;
    
    // --- UI СИСТЕМА ---
    private WindowManager _uiManager;
    private SettingsWindow _settingsWindow;
    private TimeSettingsWindow _timeSettingsWindow;
    private VisualDebugWindow _visualDebugWindow;
    private DebugStatsWindow _debugStatsWindow;
    private VoxelInspectorWindow _voxelInspectorWindow;
    private MainToolbarWindow _toolbarWindow;
    private bool _isUIMode = false; // Режим открытого меню/курсора
    // ------------------
    
    private int _reallocationDelayFrames = 0;
    
    public static List<DynamiteEntity> Entities = new List<DynamiteEntity>();
    public static void RegisterEntity(DynamiteEntity e) => Entities.Add(e);

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
        
        var playerController = new PlayerController(_physicsWorld, _camera, startPosition);
        _physicsWorld.SetPlayerHandle(playerController.BodyHandle);

        _worldManager = new WorldManager(_physicsWorld, playerController);
        _player = new Player(playerController, _camera, _worldManager);
        var invWindow = new InventoryWindow(_player);

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
        _worldManager.OnVoxelEdited += (chunk, pos, mat) => _renderer.NotifyVoxelEdited(chunk, pos, mat);

        _renderer.UploadAllVisibleChunks();

        // --- ИНИЦИАЛИЗАЦИЯ UI СИСТЕМЫ ---
        _uiManager = new WindowManager(this);
        _settingsWindow = new SettingsWindow(_worldManager, _renderer);
        _timeSettingsWindow = new TimeSettingsWindow(); // <--- ДОБАВЛЕНО
        _visualDebugWindow = new VisualDebugWindow();
        _debugStatsWindow = new DebugStatsWindow();
        _voxelInspectorWindow = new VoxelInspectorWindow(_worldManager, _camera);
        
        // Передаем новое окно в тулбар
        _toolbarWindow = new MainToolbarWindow(this, _settingsWindow, _timeSettingsWindow, _visualDebugWindow, _debugStatsWindow, _voxelInspectorWindow);

        _uiManager.AddWindow(_settingsWindow);
        _uiManager.AddWindow(_timeSettingsWindow); // <--- ДОБАВЛЕНО
        _uiManager.AddWindow(_visualDebugWindow);
        _uiManager.AddWindow(_debugStatsWindow);
        _uiManager.AddWindow(_voxelInspectorWindow);
        _uiManager.AddWindow(_toolbarWindow);
        _uiManager.AddWindow(invWindow);
        // --------------------------------------
        
        _input.ResetMouseDelta();
        _isInitialized = true;
        
        CursorState = CursorState.Grabbed;

        unsafe
        {
            var winPtr = this.WindowPtr;
            if (GLFW.RawMouseMotionSupported())
            {
                GLFW.SetInputMode(winPtr, (CursorStateAttribute)0x00033005, (CursorModeValue)1);
                var status = GLFW.GetInputMode(winPtr, (CursorStateAttribute)0x00033005);
                Console.WriteLine($"[Input] Raw Mouse Motion Request: ON. Result Status: {status} (1 = Success)");
            }
            else
            {
                Console.WriteLine("[Input] Raw Mouse Motion NOT supported by your driver!");
            }
        }
    }

    protected override void OnMouseMove(MouseMoveEventArgs e)
    {
        if (_input != null) _input.AddRawMouseDelta(e.Delta);
    }
    
    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);
        if (!_isInitialized) return;

        _input.Update(KeyboardState, MouseState);

        float deltaTime = (float)e.Time;
        
        if (GameSettings.EnableDynamicTime)
        {
            GameSettings.TotalTimeHours += (deltaTime * GameSettings.TimeScale) / 3600.0;
        }
        
        _uiManager.Update(this, deltaTime);
        
        if (_renderer.IsReallocationPending())
        {
            _reallocationDelayFrames++;
            if (_reallocationDelayFrames >= 3)
            {
                _renderer.PerformReallocation();
                _worldManager.ReloadWorld();
                _renderer.ReloadShader();
                _reallocationDelayFrames = 0;
                GC.Collect();
            }
            else return;
        }

        if (_input.IsKeyPressed(Keys.Escape))
        {
            _isUIMode = !_isUIMode;
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
            if (CursorState != CursorState.Grabbed) { CursorState = CursorState.Grabbed; _input.ResetMouseDelta(); } 
        }

        _testManager.Update(deltaTime, _input);
        
        if (!_isUIMode)
        {
            if (_input.IsKeyPressed(Keys.F)) _player.Controller.ToggleFly();
            
            // Здесь обновляется только логика (физика, таймеры, инвентарь)
            _player.Update(deltaTime, _input);
            
            for (int i = Entities.Count - 1; i >= 0; i--)
            {
                Entities[i].Update(deltaTime);
                if (Entities[i].IsDead) Entities.RemoveAt(i);
            }
        }

        if (!_renderer.IsReallocationPending())
        {
            _worldManager.Update(deltaTime);
            _physicsWorld.Update(deltaTime);
            
            // ВАЖНО: Здесь обновляем только данные чанков (тяжелая операция)
            _renderer.UpdateChunkData(deltaTime); // <--- БЫВШИЙ Update()
            
            DebugDraw.UpdateAndRender(deltaTime, _lineRenderer);
        }

        UpdateDebugStats(deltaTime);
    }
    
    private void UpdateDebugStats(float deltaTime)
    {
        // Проверка PerformanceMonitor теперь автоматически привязана к окну
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

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);
        if (!_isInitialized) return;
        _frameCount++; 
        
        // ==========================================
        // ВИЗУАЛЬНАЯ ИНТЕРПОЛЯЦИЯ (КАЖДЫЙ КАДР)
        // ==========================================
        
        // 1. Рассчитываем ViewModel прямо перед рендером
        var viewModel = _player.GetViewModel();
        if (viewModel != null)
        {
            // Параметры
            Vector3 handOffset = new Vector3(0.4f, -0.4f, -0.8f);
            Vector3 bobbing = Vector3.Zero;
            
            if (_input.GetMovementInput().LengthSquared > 0.1f)
            {
                float time = GameSettings.TimeOfDay * 20.0f; 
                bobbing.Y = (float)Math.Sin(time) * 0.015f;
                bobbing.X = (float)Math.Cos(time * 0.5f) * 0.01f;
            }

            Quaternion itemTilt = Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(10),  
                MathHelper.DegreesToRadians(-15), 
                MathHelper.DegreesToRadians(10)   
            );

            // Используем наш новый хелпер
            MathUtils.CalculateViewModelTransform(
                _camera.GetViewMatrix(),
                handOffset,
                itemTilt,
                bobbing,
                out Vector3 finalPos,
                out Quaternion finalRot
            );

            // ForceSetTransform тут работает идеально: он ставит Prev=Curr, убирая интерполяцию
            viewModel.ForceSetTransform(finalPos, finalRot);
            _renderer.SetViewModel(viewModel);
        }
        else
        {
            _renderer.SetViewModel(null);
        }

        // 2. Обновляем матрицы ВСЕХ объектов для GPU
        // Это гарантирует, что и динамит, и падающие блоки будут в позиции для ЭТОГО кадра
        if (!_renderer.IsReallocationPending())
        {
            _renderer.UpdateDynamicObjectsAndGrid();
        }

        // ==========================================
        
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _renderer.Render(_camera);
        
        _physicsDebugger.Draw(_physicsWorld, _lineRenderer, _camera);
        _lineRenderer.Render(_camera); 
        
        if (!_isUIMode) _crosshair.Render();
        
        _uiManager.Render();
        SwapBuffers();
    }
}