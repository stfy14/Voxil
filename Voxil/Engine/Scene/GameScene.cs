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
        _renderer.Render(CameraData.From(_camera));

        // Эта штука может вызвать _lineRenderer.Render(...) если рисуются коллайдеры
        _physicsDebugger.Draw(_physicsWorld, _lineRenderer, _camera);

        // --- НОВОЕ: Добавляем рамки сеток GI в очередь отрисовки ---
        if (GameSettings.EnableGI && GameSettings.ShowGIProbeGridBounds && _renderer.GISystem != null)
        {
            _renderer.GISystem.DrawDebugBounds(_lineRenderer);
        }

        // Финальный проход отрисовки линий (лучи взрывов, дебаг-линии и рамки GI)
        _lineRenderer.Render(_camera);

        _crosshair.Render();
    }

    public void OnResize(int width, int height)
    {
        _renderer.OnResize(width, height);
        _camera.UpdateAspectRatio((float)width / height);
        _crosshair.UpdateSize(width, height);
    }
}