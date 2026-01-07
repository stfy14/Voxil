using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
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
        // Нажатие 'T' запускает тест "100 вокселей"
        if (input.IsKeyPressed(Keys.T))
        {
            StartSpawnTest(100, 0.05f); // 100 штук, каждые 0.05 сек (быстрее, чем 0.2, для стресса)
            Console.WriteLine("[Test] Started spawning 100 voxels...");
        }

        // Нажатие 'Y' спавнит "Бомбу" (сразу 1000 в одной точке)
        if (input.IsKeyPressed(Keys.Y))
        {
             SpawnExplosionTest(1000);
             Console.WriteLine("[Test] SPAWNED 1000 VOXELS INSTANTLY");
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
        // Спавним перед камерой
        var spawnPos = _camera.Position + _camera.Front * 3.0f;
        
        // Добавляем немного случайности, чтобы они не падали идеальным столбом
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

        for(int i=0; i<count; i++)
        {
             // Спавним в куче 4x4x4 метра
             var pos = basePos + new OpenTK.Mathematics.Vector3(
                 (float)rnd.NextDouble() * 4,
                 (float)rnd.NextDouble() * 4,
                 (float)rnd.NextDouble() * 4
             );
             _worldManager.SpawnTestVoxel(pos.ToSystemNumerics(), MaterialType.Stone);
        }
    }
}