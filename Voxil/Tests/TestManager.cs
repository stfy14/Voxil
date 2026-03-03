using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class TestManager
{
    private readonly WorldManager _worldManager;
    private readonly Camera _camera;

    private enum TestState { Idle, WaitingForSpawn, WaitingForSplit }
    private TestState _state = TestState.Idle;
    
    private VoxelObject _testObject;
    private Vector3 _spawnPosition;
    private float _waitTimer = 0f;
    private int _lastSeconds = -1; // Для красивого вывода в консоль

    public TestManager(WorldManager worldManager, Camera camera)
    {
        _worldManager = worldManager;
        _camera = camera;
    }

    public void Update(float deltaTime, InputManager input)
    {
        // F5 запускает тест!
        if (input.IsKeyPressed(Keys.F5))
        {
            RunAutomatedBreakTest();
        }

        if (_state == TestState.Idle) return;
        
        _waitTimer -= deltaTime;

        // Печатаем обратный отсчет, чтобы понимать, что тест идет
        int currentSeconds = (int)Math.Ceiling(_waitTimer);
        if (currentSeconds != _lastSeconds && currentSeconds >= 0)
        {
            Console.WriteLine($"[Test] Waiting... {currentSeconds}s");
            _lastSeconds = currentSeconds;
        }

        if (_waitTimer > 0) return;

        if (_state == TestState.WaitingForSpawn)
        {
            // Ищем наш заспавненный объект
            // Условия:
            // 1. Дистанция до спавна < 5 метров (25.0f squared) — чтобы учесть смещение центра масс и отталкивание физики
            // 2. Количество вокселей == 3 (мы спавнили палку из 3 блоков)
            _testObject = _worldManager.GetAllVoxelObjects()
                .FirstOrDefault(o => o.VoxelCoordinates.Count == 3 && 
                                     (o.Position - _spawnPosition).LengthSquared < 25.0f);

            if (_testObject != null)
            {
                Console.WriteLine("\n=== [AUTOMATED TEST PHASE 1] ===");
                Console.WriteLine($"OBJECT FOUND! Dist: {(_testObject.Position - _spawnPosition).Length:F2}m");
                LogVoxelObjectState(_testObject, "Original Object");

                Console.WriteLine("\n[Test] Automatically breaking voxel at (1,0,0)...");
                // Ломаем центральный блок, чтобы палка развалилась на 2 части
                _worldManager.TestBreakVoxel(_testObject, new Vector3i(1, 0, 0));
                
                _state = TestState.WaitingForSplit;
                _waitTimer = 1.0f; 
            }
            else
            {
                // Дебаг: если не нашли, покажем, что вообще есть рядом
                Console.WriteLine("[Test ERROR] Target object not found within 5m!");
                Console.WriteLine("Nearby objects:");
                var nearby = _worldManager.GetAllVoxelObjects()
                    .Where(o => (o.Position - _spawnPosition).LengthSquared < 100.0f);
                
                foreach(var o in nearby)
                    Console.WriteLine($" - Obj at {o.Position} (Dist: {(o.Position - _spawnPosition).Length:F1}m), Voxels: {o.VoxelCoordinates.Count}");

                _state = TestState.Idle;
            }
        }
        else if (_state == TestState.WaitingForSplit)
        {
            Console.WriteLine("\n===[AUTOMATED TEST PHASE 2] ===");
            Console.WriteLine("OBJECTS AFTER SPLIT:");
            
            // Логируем оставшийся кусок (если он выжил)
            if (_testObject.VoxelCoordinates.Count > 0)
            {
                LogVoxelObjectState(_testObject, "Main Object (Remaining)");
            }

            // Ищем новые осколки (они должны быть рядом)
            var fragments = _worldManager.GetAllVoxelObjects()
                .Where(o => o != _testObject && (o.Position - _testObject.Position).LengthSquared < 25.0f);

            int fragIndex = 1;
            foreach (var frag in fragments)
            {
                LogVoxelObjectState(frag, $"Fragment #{fragIndex++}");
            }
            
            Console.WriteLine("=== [AUTOMATED TEST FINISHED] ===\n");
            _state = TestState.Idle;
        }
    }

    private void RunAutomatedBreakTest()
    {
        if (_state != TestState.Idle)
        {
            Console.WriteLine("[Test] Test already in progress!");
            return;
        }

        // Создаем палку 3x1x1 (три разных материала)
        var voxels = new List<Vector3i> { new(0, 0, 0), new(1, 0, 0), new(2, 0, 0) };
        var materials = new Dictionary<Vector3i, uint>
        {
            [new(0,0,0)] = (uint)MaterialType.Dirt,
            [new(1,0,0)] = (uint)MaterialType.Stone,
            [new(2,0,0)] = (uint)MaterialType.Wood,
        };

        _spawnPosition = _camera.Position + _camera.Front * 5.0f;
        
        Console.WriteLine($"\n[Test] Spawning 3x1x1 block at {_spawnPosition}...");
        _worldManager.SpawnComplexObject(_spawnPosition.ToSystemNumerics(), voxels, MaterialType.Stone, materials);
        
        _state = TestState.WaitingForSpawn;
        _waitTimer = 1.0f; 
        _lastSeconds = -1;
    }

    private void LogVoxelObjectState(VoxelObject vo, string prefix)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{prefix}]");
        sb.AppendLine($"  Position : {vo.Position}");
        sb.AppendLine($"  Rotation : {vo.Rotation.Xyz}, {vo.Rotation.W}");
        sb.AppendLine($"  Scale    : {vo.Scale}");
        sb.AppendLine($"  CenterOfMass (Local) : {vo.LocalCenterOfMass}");
        sb.AppendLine($"  Voxel Count : {vo.VoxelCoordinates.Count}");
        
        sb.AppendLine("  Voxels & Materials:");
        foreach (var voxelPos in vo.VoxelCoordinates.OrderBy(v => v.X).ThenBy(v => v.Y).ThenBy(v => v.Z))
        {
            vo.VoxelMaterials.TryGetValue(voxelPos, out uint matId);
            sb.AppendLine($"    - {voxelPos} -> {(MaterialType)matId}");
        }
        Console.WriteLine(sb.ToString());
    }
}