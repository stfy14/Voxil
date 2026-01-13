// --- START OF FILE TestManager.cs ---
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic; // Не забудь добавить
using System.Diagnostics;

public class TestManager
{
    private readonly WorldManager _worldManager;
    private readonly Camera _camera;

    private bool _isSpawning = false;
    private int _spawnCount = 0;
    private int _targetSpawnCount = 0;
    private float _spawnTimer = 0f;
    private float _spawnDelay = 0.2f;

    public TestManager(WorldManager worldManager, Camera camera)
    {
        _worldManager = worldManager;
        _camera = camera;
    }

    public void Update(float deltaTime, InputManager input)
    {
        // 'T' - Поштучный спавн (стресс-тест количества объектов)
        if (input.IsKeyPressed(Keys.T))
        {
            StartSpawnTest(100, 0.05f); 
            Console.WriteLine("[Test] Started spawning 100 voxels...");
        }

        // 'Y' - Взрыв (много мелких объектов в одной куче - стресс для GPU Grid)
        if (input.IsKeyPressed(Keys.Y))
        {
             SpawnExplosionTest(1000);
             Console.WriteLine("[Test] SPAWNED 1000 VOXELS INSTANTLY (Explosion)");
        }

        // 'U' - Большой куб (1000 вокселей в одном теле - тест Greedy Meshing)
        if (input.IsKeyPressed(Keys.U))
        {
            SpawnLargeCubeTest(10); // 10x10x10
            Console.WriteLine("[Test] SPAWNED LARGE CUBE 10x10x10");
        }

        if (_isSpawning)
        {
            _spawnTimer += deltaTime;
            while (_spawnTimer >= _spawnDelay && _spawnCount < _targetSpawnCount)
            {
                _spawnTimer -= _spawnDelay;
                SpawnSingleVoxel();
                _spawnCount++;
            }

            if (_spawnCount >= _targetSpawnCount)
            {
                _isSpawning = false;
                Console.WriteLine("[Test] Spawning finished.");
            }
        }
    }

    private void StartSpawnTest(int count, float delay)
    {
        _targetSpawnCount = count;
        _spawnCount = 0;
        _spawnDelay = delay;
        _spawnTimer = 0f;
        _isSpawning = true;
    }

    private void SpawnSingleVoxel()
    {
        var spawnPos = _camera.Position + _camera.Front * 3.0f;
        var rnd = new Random();
        float offset = 0.5f;
        spawnPos.X += (float)(rnd.NextDouble() * offset * 2 - offset);
        spawnPos.Z += (float)(rnd.NextDouble() * offset * 2 - offset);

        _worldManager.SpawnTestVoxel(spawnPos.ToSystemNumerics(), MaterialType.Wood);
    }

    private void SpawnExplosionTest(int count)
    {
        var rnd = new Random();
        var basePos = _camera.Position + _camera.Front * 5.0f;

        // Спавним 1000 ОТДЕЛЬНЫХ объектов
        for(int i=0; i<count; i++)
        {
             var pos = basePos + new OpenTK.Mathematics.Vector3(
                 (float)rnd.NextDouble() * 4,
                 (float)rnd.NextDouble() * 4,
                 (float)rnd.NextDouble() * 4
             );
             _worldManager.SpawnTestVoxel(pos.ToSystemNumerics(), MaterialType.Stone);
        }
    }

    private void SpawnLargeCubeTest(int size)
    {
        // Генерируем список координат для одного большого объекта
        List<OpenTK.Mathematics.Vector3i> voxels = new List<OpenTK.Mathematics.Vector3i>();
        for (int x = 0; x < size; x++)
        {
            for (int y = 0; y < size; y++)
            {
                for (int z = 0; z < size; z++)
                {
                    voxels.Add(new OpenTK.Mathematics.Vector3i(x, y, z));
                }
            }
        }

        // Спавним перед камерой
        var spawnPos = (_camera.Position + _camera.Front * 5.0f).ToSystemNumerics();
        
        // Используем метод создания объекта напрямую через WorldManager (нужно добавить метод или использовать очередь)
        // В WorldManager уже есть метод CreateDetachedObject, но он удаляет воксели из мира.
        // Нам нужно просто создать новый. Добавим воксели в очередь создания.
        
        // Для этого нам придется хакнуть/расширить WorldManager, чтобы он принимал готовый список
        // В твоем коде есть структура VoxelObjectCreationData.
        
        // Внимание: мы используем приватную структуру через публичный метод, который сейчас добавим в WorldManager (см. ниже).
        _worldManager.SpawnComplexObject(spawnPos, voxels, MaterialType.Wood);
    }
}