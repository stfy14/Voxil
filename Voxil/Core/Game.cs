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

    private Player _player;
    
    private WindowManager _uiManager;
    private SettingsWindow _settingsWindow;
    private MainMenuWindow _mainMenuWindow;
    private DebugStatsWindow _debugStatsWindow;
    
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
        
        // Создаем контроллер и менеджера мира
        var playerController = new PlayerController(_physicsWorld, _camera, startPosition);
        _physicsWorld.SetPlayerHandle(playerController.BodyHandle);

        _worldManager = new WorldManager(_physicsWorld, playerController);

        // --- ИНИЦИАЛИЗАЦИЯ ИГРОКА ---
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

        _uiManager = new WindowManager(this);
        _settingsWindow = new SettingsWindow(_worldManager, _renderer);
        _mainMenuWindow = new MainMenuWindow(this, _settingsWindow);
        _debugStatsWindow = new DebugStatsWindow();

        _uiManager.AddWindow(_settingsWindow);
        _uiManager.AddWindow(_mainMenuWindow);
        _uiManager.AddWindow(_debugStatsWindow);
        _uiManager.AddWindow(invWindow);
        
        _input.ResetMouseDelta();
        
        _isInitialized = true;
        
        // === 1. СНАЧАЛА ХВАТАЕМ КУРСОР ===
        CursorState = CursorState.Grabbed;

        // === 2. ПОТОМ ВКЛЮЧАЕМ RAW INPUT ===
        // Это единственный правильный способ для твоей версии OpenTK
        unsafe
        {
            var winPtr = this.WindowPtr;
            
            // Проверяем, поддерживает ли железо/драйвер этот режим
            if (GLFW.RawMouseMotionSupported())
            {
                // 0x00033005 = GLFW_RAW_MOUSE_MOTION
                // (CursorModeValue)1 = GLFW_TRUE
                GLFW.SetInputMode(winPtr, (CursorStateAttribute)0x00033005, (CursorModeValue)1);
                
                // === ПРОВЕРКА: Проверяем, включилось ли на самом деле ===
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
        // base.OnMouseMove(e); // <--- УБРАТЬ (снижает нагрузку на CPU)
        
        if (_input != null)
        {
            _input.AddRawMouseDelta(e.Delta);
        }
    }
    
    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);
        if (!_isInitialized) return;

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

        bool isMenuOpen = _mainMenuWindow.IsVisible || _settingsWindow.IsVisible;
        // Говорим инпуту, можно ли сейчас крутить камерой
        _input.SetCursorGrabbed(!isMenuOpen); 

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

        _input.Update(KeyboardState, MouseState);
        if (_input.IsKeyPressed(Keys.Escape))
        {
            if (_settingsWindow.IsVisible) _settingsWindow.Toggle();
            else _mainMenuWindow.Toggle();
        }
        _testManager.Update(deltaTime, _input);
        
        if (!isMenuOpen)
        {
            if (_input.IsKeyPressed(Keys.F3)) { PerformanceMonitor.IsEnabled = !PerformanceMonitor.IsEnabled; _debugStatsWindow.Toggle(); }
            if (_input.IsKeyPressed(Keys.F)) _player.Controller.ToggleFly();
            
            // АНИМАЦИЯ РУК И ВЬЮМОДЕЛИ
            var viewModel = _player.GetViewModel();
            if (viewModel != null)
            {
                // Получаем матрицу "Камера -> Мир" (обратная ViewMatrix)
                Matrix4 cameraWorld = _camera.GetViewMatrix().Inverted();

                // Позиция "Хвата" относительно камеры
                // Немного пододвинем ближе и правее
                Vector3 handOffset = new Vector3(0.5f, -0.4f, -0.7f); 

                // Покачивание при ходьбе
                if (_input.GetMovementInput().LengthSquared > 0.1f)
                {
                    // Используем TimeOfDay, чтобы анимация была плавной, а не дерганой
                    float time = GameSettings.TimeOfDay * 20.0f; 
                    handOffset.Y += (float)Math.Sin(time) * 0.02f;
                    handOffset.X += (float)Math.Cos(time * 0.5f) * 0.015f;
                }

                // Трансформируем смещение в мировые координаты
                Vector3 worldPos = Vector3.TransformPosition(handOffset, cameraWorld);

                // Поворот предмета: берем поворот камеры и добавляем наклон
                Quaternion itemTilt = Quaternion.FromEulerAngles(
                    MathHelper.DegreesToRadians(5),   // Небольшой наклон вперед
                    MathHelper.DegreesToRadians(-10), // Поворот к центру
                    MathHelper.DegreesToRadians(5)    // Наклон вбок
                );
                Quaternion finalRot = _camera.Rotation * itemTilt;

                viewModel.ForceSetTransform(worldPos, finalRot);
                _renderer.SetViewModel(viewModel);
            }
            else
            {
                _renderer.SetViewModel(null);
            }
            
            // --- ОБНОВЛЕНИЕ ИГРОКА ---
            _player.Update(deltaTime, _input);
            
            // --- ОБНОВЛЕНИЕ ДИНАМИТА ---
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
        
        // АНИМАЦИЯ РУК
        var viewModel = _player.GetViewModel();
        if (viewModel != null)
        {
            // Настройки позиции "Хвата" относительно камеры
            // X: +Вправо
            // Y: +Вверх (относительно взгляда)
            // Z: +Назад (к игроку), -Вперед (от игрока)
            
            // Ставим руку справа снизу и чуть вперед
            Vector3 handOffset = new Vector3(0.4f, -0.5f, -0.8f);

            // Покачивание при ходьбе (Bobbing)
            if (_input.IsKeyDown(Keys.W) || _input.IsKeyDown(Keys.A) || _input.IsKeyDown(Keys.S) || _input.IsKeyDown(Keys.D))
            {
                float time = (float)GameSettings.TotalTimeHours * 6000.0f; 
                handOffset.Y += (float)Math.Sin(time) * 0.015f; // Вверх-вниз
                handOffset.X += (float)Math.Cos(time * 0.5f) * 0.01f; // Влево-вправо
            }

            // 1. Позиция: Берем позицию камеры и прибавляем смещение, повернутое вместе с камерой
            // Это "приклеивает" точку хвата к камере
            Vector3 worldPos = _camera.Position + Vector3.Transform(handOffset, _camera.Rotation);

            // 2. Поворот: Берем поворот камеры и доворачиваем сам предмет
            // Наклоним динамит чуть вперед (X) и влево (Y), чтобы было видно верхушку
            Quaternion itemTilt = Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(10),  // Наклон вперед
                MathHelper.DegreesToRadians(-15), // Поворот влево (к центру экрана)
                MathHelper.DegreesToRadians(10)   // Чуть наклон вбок
            );
            
            Quaternion finalRot = _camera.Rotation * itemTilt;

            // 3. Применяем
            viewModel.ForceSetTransform(worldPos, finalRot);
            _renderer.SetViewModel(viewModel);
        }
        else
        {
            _renderer.SetViewModel(null);
        }

        _renderer.Render(_camera);
        
        _physicsDebugger.Draw(_physicsWorld, _lineRenderer, _camera);
        _lineRenderer.Render(_camera); 
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