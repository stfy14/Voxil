using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;

public class Game : GameWindow
{
    private Camera _camera;
    private InputManager _input;
    private PhysicsWorld _physicsWorld;
    private WorldManager _worldManager;
    private PlayerController _playerController;
    private GpuRaycastingRenderer _renderer;
    
    private DebugOverlay _debugOverlay;
    private LineRenderer _lineRenderer;
    private PhysicsDebugDrawer _physicsDebugger;
    private Crosshair _crosshair;
    private bool _showDebugOverlay = true;
    
    // --- ИЗМЕНЕНИЕ 1: Добавили счетчик кадров ---
    private int _frameCount = 0; 
    private float _debugUpdateTimer = 0f;
    
    private List<string> _debugLines = new List<string>();
    private bool _isInitialized = false;
    private TestManager _testManager;

    public Game(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        GL.ClearColor(0.6f, 0.7f, 0.9f, 1.0f);
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);

        _physicsWorld = new PhysicsWorld();

        var startPosition = new System.Numerics.Vector3(8, 60, 8);
        _camera = new Camera(VectorExtensions.ToOpenTK(startPosition), Size.X / (float)Size.Y);
        _playerController = new PlayerController(_physicsWorld, _camera, startPosition);
        _physicsWorld.SetPlayerHandle(_playerController.BodyHandle);
        _worldManager = new WorldManager(_physicsWorld, _playerController);

        _input = new InputManager();
        CursorState = CursorState.Grabbed;

        _debugOverlay = new DebugOverlay(Size.X, Size.Y);
        _lineRenderer = new LineRenderer();
        _physicsDebugger = new PhysicsDebugDrawer();
        _crosshair = new Crosshair(Size.X, Size.Y);
        _testManager = new TestManager(_worldManager, _camera);

        _renderer = new GpuRaycastingRenderer(_worldManager);
        _renderer.Load();

        _renderer.OnResize(Size.X, Size.Y);

        // --- ПОДПИСКИ НА СОБЫТИЯ ---
        _worldManager.OnChunkLoaded += (chunk) => _renderer.NotifyChunkLoaded(chunk);
        _worldManager.OnChunkModified += (chunk) => _renderer.NotifyChunkLoaded(chunk);
        _worldManager.OnVoxelFastDestroyed += (pos) => 
        {
            var chunk = _worldManager.GetAllChunks().GetValueOrDefault(GetChunkPos(pos));
            if(chunk != null) _renderer.NotifyChunkLoaded(chunk);
        };
        _worldManager.OnChunkUnloaded += (pos) => _renderer.UnloadChunk(pos);

        _renderer.UploadAllVisibleChunks();

        _isInitialized = true;
        Console.WriteLine("[Game] Initialized (Pure GPU Raycasting).");
    }

    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);
        if (!_isInitialized) return;
        float deltaTime = (float)e.Time;

        _input.Update(KeyboardState, MouseState);

        if (_input.IsExitPressed())
        {
            Close();
            return;
        }
        
        // J - Переключение режима теней
        if (_input.IsKeyPressed(Keys.J))
        {
            GameSettings.CurrentShadowMode++;
            if ((int)GameSettings.CurrentShadowMode > 2) 
                GameSettings.CurrentShadowMode = ShadowMode.None;
            _renderer.ReloadShader(); 
            Console.WriteLine($"[Shadow Mode] Set to: {GameSettings.CurrentShadowMode}");
        }

        // [ - Уменьшить сэмплы
        if (_input.IsKeyPressed(Keys.LeftBracket))
        {
            GameSettings.SoftShadowSamples = Math.Max(2, GameSettings.SoftShadowSamples / 2);
            Console.WriteLine($"[Soft Shadows] Samples: {GameSettings.SoftShadowSamples}");
        }

        // ] - Увеличить сэмплы
        if (_input.IsKeyPressed(Keys.RightBracket))
        {
            GameSettings.SoftShadowSamples = Math.Min(64, GameSettings.SoftShadowSamples * 2);
            Console.WriteLine($"[Soft Shadows] Samples: {GameSettings.SoftShadowSamples}");
        }

        // H - Вода
        if (_input.IsKeyPressed(Keys.H))
        {
            GameSettings.UseProceduralWater = !GameSettings.UseProceduralWater;
            _renderer.ReloadShader();
            Console.WriteLine($"[Water Mode] Set to: {(GameSettings.UseProceduralWater ? "Procedural" : "Texture")}");
        }
        
        // V - AO
        if (_input.IsKeyPressed(Keys.V))
        {
            GameSettings.EnableAO = !GameSettings.EnableAO;
            _renderer.ReloadShader();
            Console.WriteLine($"[AO] Set to: {GameSettings.EnableAO}");
        }
        
        // B - Прозрачность воды
        if (_input.IsKeyPressed(Keys.B))
        {
            GameSettings.EnableWaterTransparency = !GameSettings.EnableWaterTransparency;
            _renderer.ReloadShader();
            Console.WriteLine($"[Water Transparency] Set to: {GameSettings.EnableWaterTransparency}");
        }

        // G - Beam Optimization
        if (_input.IsKeyPressed(Keys.G)) 
        {
            GameSettings.BeamOptimization = !GameSettings.BeamOptimization;
            _renderer.ReloadShader();
            Console.WriteLine($"[Beam Optimization] Set to: {GameSettings.BeamOptimization}");
        }
        
        if (_input.IsKeyPressed(Keys.F3))
        {
            PerformanceMonitor.IsEnabled = !PerformanceMonitor.IsEnabled;
            _showDebugOverlay = PerformanceMonitor.IsEnabled;
            if (!PerformanceMonitor.IsEnabled) _debugLines.Clear();
        }

        // Settings Hotkeys
        if (_input.IsKeyPressed(Keys.O)) GameSettings.RenderDistance = Math.Max(4, GameSettings.RenderDistance - 4);
        if (_input.IsKeyPressed(Keys.P)) GameSettings.RenderDistance = Math.Min(128, GameSettings.RenderDistance + 4);

        if (_input.IsKeyPressed(Keys.K)) { GameSettings.GenerationThreads = Math.Max(1, GameSettings.GenerationThreads - 1); _worldManager.SetGenerationThreadCount(GameSettings.GenerationThreads); }
        if (_input.IsKeyPressed(Keys.L)) { GameSettings.GenerationThreads = Math.Min(16, GameSettings.GenerationThreads + 1); _worldManager.SetGenerationThreadCount(GameSettings.GenerationThreads); }

        if (_input.IsKeyPressed(Keys.N)) { GameSettings.PhysicsThreads = Math.Max(1, GameSettings.PhysicsThreads - 1); _physicsWorld.SetThreadCount(GameSettings.PhysicsThreads); }
        if (_input.IsKeyPressed(Keys.M)) { GameSettings.PhysicsThreads = Math.Min(16, GameSettings.PhysicsThreads + 1); _physicsWorld.SetThreadCount(GameSettings.PhysicsThreads); }

        if (_input.IsKeyPressed(Keys.U)) GameSettings.GpuUploadSpeed = Math.Max(1, GameSettings.GpuUploadSpeed - 1);
        if (_input.IsKeyPressed(Keys.I)) GameSettings.GpuUploadSpeed = Math.Min(200, GameSettings.GpuUploadSpeed + 1);

        _testManager.Update((float)e.Time, _input);
        _playerController.Update(_input, deltaTime);
        _worldManager.Update(deltaTime);
        _physicsWorld.Update(deltaTime);
        _worldManager.ProcessVoxelObjects();
        _renderer.Update(deltaTime);

        if (_input.IsMouseButtonPressed(MouseButton.Left))
        {
            ProcessBlockDestruction();
        }
        
        UpdateDebugStats(deltaTime);
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

    private void UpdateDebugStats(float deltaTime)
    {
        if (!PerformanceMonitor.IsEnabled) return;

        _debugUpdateTimer += deltaTime;
        
        // --- ИЗМЕНЕНИЕ 2: Обновляем текст раз в 0.5 сек для читаемости ---
        if (_debugUpdateTimer >= 0.5f)
        {
            // Считаем средний FPS за прошедшее время (количество кадров / время)
            float avgFps = (float)_frameCount / _debugUpdateTimer;
            
            var averages = PerformanceMonitor.GetDataAndReset(_debugUpdateTimer);
            
            _debugLines.Clear();
            _debugLines.Add($"FPS: {avgFps:F0}"); // Стабильное значение
            _debugLines.Add($"VoxelSize: {Constants.VoxelSize:F3}");
            _debugLines.Add($"Water: {(GameSettings.UseProceduralWater ? "Procedural" : "Texture")}");

            _debugLines.Add($"Pos: {_camera.Position.X:F0} {_camera.Position.Y:F0} {_camera.Position.Z:F0}");
            
            _debugLines.Add("--- Settings ---");
            _debugLines.Add($"[O/P] Dist:   {GameSettings.RenderDistance} chunks");
            _debugLines.Add($"[K/L] Gen Th: {GameSettings.GenerationThreads}");
            _debugLines.Add($"[N/M] Phys Th:{GameSettings.PhysicsThreads}");
            _debugLines.Add($"[U/I] GPU Up: {GameSettings.GpuUploadSpeed}/frame");

            _debugLines.Add("--- Perf (Avg Time | Rate) ---");
            if (averages != null)
            {
                foreach(var kvp in averages) 
                {
                    _debugLines.Add($"{kvp.Key}: {kvp.Value}");
                }
            }
            
            // Сбрасываем таймеры
            _debugUpdateTimer = 0f;
            _frameCount = 0;
        }
    }

    private void DrawPhysicsDebug()
    {
        _physicsDebugger.DrawVoxelObjects(_physicsWorld, _worldManager.GetAllVoxelObjects(), _lineRenderer);
    }

    private Vector3i GetChunkPos(Vector3i worldPos) => new Vector3i(
        (int)Math.Floor(worldPos.X / 16f), 
        (int)Math.Floor(worldPos.Y / 16f), 
        (int)Math.Floor(worldPos.Z / 16f));

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);
        if (!_isInitialized) return;
        
        // --- ИЗМЕНЕНИЕ 3: Считаем кадры здесь ---
        _frameCount++; 

        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _renderer.Render(_camera);
        if (_showDebugOverlay)
        {
            DrawPhysicsDebug();
            _lineRenderer.Render(_camera);
        }
        _crosshair.Render();
        if (_showDebugOverlay) _debugOverlay.Render(_debugLines);

        SwapBuffers();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, Size.X, Size.Y);
        _camera?.UpdateAspectRatio(Size.X / (float)Size.Y);
        _renderer?.OnResize(Size.X, Size.Y);
        _debugOverlay?.UpdateScreenSize(Size.X, Size.Y);
        _crosshair?.UpdateSize(Size.X, Size.Y);
    }

    protected override void OnUnload()
    {
        base.OnUnload();
        _renderer?.Dispose();
        _worldManager?.Dispose();
        _physicsWorld?.Dispose();
        _debugOverlay?.Dispose();
        _lineRenderer.Dispose();
        _crosshair?.Dispose();
    }
}