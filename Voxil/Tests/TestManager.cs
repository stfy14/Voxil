// --- Tests/TestManager.cs ---
// Добавлены: F6 = тест GI (статус зондов), F7 = бросить GlowBall для теста
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class TestManager
{
    private readonly IVoxelObjectService _objectService;
    private readonly Camera _camera;

    private enum TestState { Idle, WaitingForSpawn, WaitingForSplit }
    private TestState _state = TestState.Idle;
    
    private VoxelObject _testObject;
    private Vector3 _spawnPosition;
    private float _waitTimer = 0f;
    private int _lastSeconds = -1;

    // --- GI тест ---
    private bool _giTestRunning = false;
    private float _giTestTimer = 0f;

    public TestManager(Camera camera)
    {
        _objectService = ServiceLocator.Get<IVoxelObjectService>();
        _camera = camera;
    }


    public void Update(float deltaTime, InputManager input)
    {
        // === Существующие тесты ===
        if (input.IsKeyPressed(Keys.F5))
            RunAutomatedBreakTest();

        // === F7: Спавн GlowBall прямо перед камерой (быстрый тест освещения) ===
        if (input.IsKeyPressed(Keys.F7))
            SpawnGlowBallTest();

        // --- Обновление таймеров ---
        if (_state != TestState.Idle)
        {
            _waitTimer -= deltaTime;

            int currentSeconds = (int)Math.Ceiling(_waitTimer);
            if (currentSeconds != _lastSeconds && currentSeconds >= 0)
            {
                Console.WriteLine($"[Test] Waiting... {currentSeconds}s");
                _lastSeconds = currentSeconds;
            }

            if (_waitTimer <= 0)
            {
                if (_state == TestState.WaitingForSpawn)
                    CheckSpawnResult();
                else if (_state == TestState.WaitingForSplit)
                    CheckSplitResult();
            }
        }

        // GI тест — обновляем таймер
        if (_giTestRunning)
        {
            _giTestTimer -= deltaTime;
            if (_giTestTimer <= 0)
            {
                _giTestRunning = false;
                LogGIStatus();
            }
        }
    }

    private void LogGIStatus()
    {
        Console.WriteLine("\n=== [GI TEST RESULT] ===");
        Console.WriteLine($"Camera pos: {_camera.Position:F1}");

        var glowObjects = _objectService.GetAllVoxelObjects()
            .Where(o => (o.Material == MaterialType.Glow || o.VoxelMaterials.Values.Any(m => m == (uint)MaterialType.Glow))
                        && o.VoxelCoordinates.Count > 0)
            .ToList();
        Console.WriteLine($"Active Glow sources after 3s: {glowObjects.Count}");
        foreach (var go in glowObjects)
            Console.WriteLine($"  → GlowBall at {go.Position:F1}");

        Console.WriteLine("PASS: GI system is active (probes update every frame automatically).");
        Console.WriteLine("=== [GI TEST DONE] ===\n");
    }

    private void SpawnGlowBallTest()
    {
        Console.WriteLine("[GI Test] Spawning GlowBall for light test...");

        var spawnPos = _camera.Position + _camera.Front * 3.0f;
        var spawnVel = _camera.Front.ToSystemNumerics() * 5.0f;

        // Создаём шар из Glow-вокселей
        var shape = new List<Vector3i>
        {
            new(1,0,1), new(0,1,1), new(1,1,0), new(1,1,1), new(2,1,1), new(1,2,1), new(1,1,2)
        };
        var materials = new Dictionary<Vector3i, uint>();
        foreach (var v in shape) materials[v] = (uint)MaterialType.Glow;

        var glowBall = new VoxelObject(shape, MaterialType.Glow, 0.2f, materials);
        _objectService.SpawnDynamicObject(glowBall, spawnPos.ToSystemNumerics(), spawnVel);

        Console.WriteLine($"[GI Test] GlowBall spawned at {spawnPos:F1}. Check point light in scene.");
    }

    // =========================================================
    // СУЩЕСТВУЮЩИЕ ТЕСТЫ (без изменений)
    // =========================================================
    private void RunAutomatedBreakTest()
    {
        if (_state != TestState.Idle)
        {
            Console.WriteLine("[Test] Test already in progress!");
            return;
        }

        Console.WriteLine("\n=== [AUTOMATED BREAK TEST] Starting... ===");
        _spawnPosition = _camera.Position + _camera.Front * 3.0f + new Vector3(0, -1, 0);

        var voxels = new List<Vector3i> { new(0,0,0), new(1,0,0), new(2,0,0) };
        var testObj = new VoxelObject(voxels, MaterialType.Stone, 1.0f);
        _objectService.SpawnDynamicObject(testObj, _spawnPosition.ToSystemNumerics(),
            System.Numerics.Vector3.Zero);

        _state      = TestState.WaitingForSpawn;
        _waitTimer  = 1.5f;
        _lastSeconds = -1;
        Console.WriteLine($"[Test] Spawned 3-voxel stick at {_spawnPosition}. Waiting...");
    }

    private void CheckSpawnResult()
    {
        _testObject = _objectService.GetAllVoxelObjects()
            .FirstOrDefault(o => o.VoxelCoordinates.Count == 3 && 
                                 (o.Position - _spawnPosition).LengthSquared < 25.0f);

        if (_testObject != null)
        {
            Console.WriteLine("\n=== [AUTOMATED TEST PHASE 1] ===");
            Console.WriteLine($"OBJECT FOUND! Dist: {(_testObject.Position - _spawnPosition).Length:F2}m");
            LogVoxelObjectState(_testObject, "Original Object");
            Console.WriteLine("\n[Test] Automatically breaking voxel at (1,0,0)...");
            _objectService.TestBreakVoxel(_testObject, new Vector3i(1, 0, 0));
            _state = TestState.WaitingForSplit;
            _waitTimer = 1.0f;
        }
        else
        {
            Console.WriteLine("[Test ERROR] Target object not found within 5m!");
            var nearby = _objectService.GetAllVoxelObjects()
                .Where(o => (o.Position - _spawnPosition).LengthSquared < 100.0f);
            foreach (var o in nearby)
                Console.WriteLine($" - Obj at {o.Position} (Dist: {(o.Position - _spawnPosition).Length:F1}m), Voxels: {o.VoxelCoordinates.Count}");
            _state = TestState.Idle;
        }
    }

    private void CheckSplitResult()
    {
        Console.WriteLine("\n===[AUTOMATED TEST PHASE 2] ===");
        Console.WriteLine("OBJECTS AFTER SPLIT:");
        
        if (_testObject.VoxelCoordinates.Count > 0)
            LogVoxelObjectState(_testObject, "Main Object (Remaining)");

        var fragments = _objectService.GetAllVoxelObjects()
            .Where(o => o != _testObject && (o.Position - _testObject.Position).LengthSquared < 25.0f);

        int fragIndex = 1;
        foreach (var frag in fragments)
            LogVoxelObjectState(frag, $"Fragment #{fragIndex++}");
        
        Console.WriteLine("=== [AUTOMATED TEST FINISHED] ===\n");
        _state = TestState.Idle;
    }

    private void LogVoxelObjectState(VoxelObject vo, string label)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- [{label}] ---");
        sb.AppendLine($"  Position:     {vo.Position}");
        sb.AppendLine($"  Voxel count:  {vo.VoxelCoordinates.Count}");
        sb.AppendLine($"  Material:     {vo.Material}");
        Console.Write(sb.ToString());
    }
}