// --- START OF FILE GameScene.cs ---
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;

public class GameScene : IScene
{
    private readonly WorldManager _worldManager;
    private readonly PhysicsWorld _physicsWorld;
    private readonly GpuRaycastingRenderer _renderer;
    private readonly Player _player;
    private readonly EntityManager _entityManager;
    private readonly Camera _camera;
    private readonly InputManager _input;
    private readonly LineRenderer _lineRenderer;
    private readonly PhysicsDebugDrawer _physicsDebugger;
    private readonly Crosshair _crosshair;
    private readonly TestManager _testManager;

    private readonly EditorGridRenderer _gridRenderer;

    public GameScene(
        WorldManager worldManager,
        PhysicsWorld physicsWorld,
        GpuRaycastingRenderer renderer,
        Player player,
        EntityManager entityManager,
        Camera camera,
        InputManager input,
        LineRenderer lineRenderer,
        PhysicsDebugDrawer physicsDebugger,
        Crosshair crosshair,
        TestManager testManager)
    {
        _worldManager = worldManager;
        _physicsWorld = physicsWorld;
        _renderer = renderer;
        _player = player;
        _entityManager = entityManager;
        _camera = camera;
        _input = input;
        _lineRenderer = lineRenderer;
        _physicsDebugger = physicsDebugger;
        _crosshair = crosshair;
        _testManager = testManager;

        _gridRenderer = new EditorGridRenderer();
    }

    public void OnEnter()
    {
        Console.WriteLine("[GameScene] Entered.");
    }

    public void OnExit()
    {
        Console.WriteLine("[GameScene] Exited.");
    }

    public void Update(float deltaTime, InputManager input)
    {
        if (input.IsKeyPressed(Keys.F)) _player.Controller.ToggleFly();

        _player.Update(deltaTime, input);
        _entityManager.Update(deltaTime);
        _testManager.Update(deltaTime, input);

        _worldManager.Update(deltaTime);
        _physicsWorld.Update(deltaTime);
        _renderer.UpdateChunkData(deltaTime);

        DebugDraw.UpdateAndRender(deltaTime, _lineRenderer);
    }

    public void Render()
    {
        var camData = CameraData.From(_camera);
        _renderer.Render(camData);

        // --- ЗАПУСКАЕМ ДЕБАГ ПАСС (Тут доступен Z-Buffer от гор!) ---
        _renderer.BeginDebugPass();

        _physicsDebugger.Draw(_physicsWorld, _lineRenderer, _camera);

        if (GameSettings.EnableGI && GameSettings.ShowGIProbeGridBounds && _renderer.GISystem != null)
        {
            _renderer.GISystem.DrawDebugGridBounds(_gridRenderer, camData);
        }

        _lineRenderer.Render(_camera);

        _renderer.EndDebugPass();
        // -------------------------------------------------------------

        // Прицел рисуем прямо на экран
        _crosshair.Render();
    }

    public void OnResize(int width, int height)
    {
        _renderer.OnResize(width, height);
        _camera.UpdateAspectRatio((float)width / height);
        _crosshair.UpdateSize(width, height);
    }
}