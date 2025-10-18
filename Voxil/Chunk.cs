// Chunk.cs
using BepuPhysics;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

public class Chunk : IDisposable
{
    public const int ChunkSize = 16;
    // Массив вокселей хранит "сырые" данные о мире.
    private readonly Voxel[,,] _voxels = new Voxel[ChunkSize, ChunkSize, ChunkSize];
    // Список VoxelObject представляет собой "собранные" из вокселей физические объекты.
    private readonly List<VoxelObject> _voxelObjects = new();

    private readonly PhysicsWorld _physicsWorld;
    public Vector3i Position { get; }

    public Chunk(Vector3i position, PhysicsWorld physicsWorld)
    {
        Position = position;
        _physicsWorld = physicsWorld;
        ProceduralGenerate();
    }

    /// <summary>
    /// Первичная генерация мира в этом чанке.
    /// </summary>
    private void ProceduralGenerate()
    {
        // Создадим один большой VoxelObject, чтобы продемонстрировать падение
        var initialObject = new VoxelObject();

        // Заполним куб 8x8x8 в углу чанка
        for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
                for (int z = 0; z < 8; z++)
                {
                    _voxels[x, y, z] = new Voxel(MaterialType.Stone);
                    initialObject.VoxelCoordinates.Add(new Vector3i(x, y, z));
                }

        if (initialObject.VoxelCoordinates.Count > 0)
        {
            // Определяем стартовую позицию объекта в мировых координатах
            var worldPosition = new System.Numerics.Vector3(
                Position.X * ChunkSize + 4,
                Position.Y * ChunkSize + 50, // Поднимем повыше, чтобы он красиво упал
                Position.Z * ChunkSize + 4
            );

            // Просим мир физики создать для него тело
            var handle = _physicsWorld.CreateVoxelObjectBody(initialObject.VoxelCoordinates, worldPosition, out var localCenterOfMass);

            // Инициализируем наш рендер-объект полученным handle
            initialObject.Initialize(handle, localCenterOfMass.ToOpenTK());
            _voxelObjects.Add(initialObject);
        }

        Console.WriteLine($"[Chunk {Position}] Сгенерирован с {_voxelObjects.Count} объектами.");
    }

    /// <summary>
    /// Главный метод разрушения. Находит VoxelObject по BodyHandle и точку попадания,
    /// а затем запускает процесс разрушения.
    /// </summary>
    public void DestroyVoxelAt(BodyHandle bodyHandle, System.Numerics.Vector3 worldHitLocation, System.Numerics.Vector3 worldHitNormal)
    {
        // 1. Находим VoxelObject, в который мы попали.
        VoxelObject targetObject = null;
        foreach (var obj in _voxelObjects)
        {
            if (obj.BodyHandle == bodyHandle)
            {
                targetObject = obj;
                break;
            }
        }

        if (targetObject == null) return; // Не нашли объект, выходим.

        // Сдвигаем точку попадания на крошечное расстояние ВНУТРЬ объекта.
        // Мы вычитаем, потому что нормаль направлена НАРУЖУ от поверхности.
        var adjustedWorldHitLocation = worldHitLocation - worldHitNormal * 0.001f;

        // 2. Конвертируем СКОРРЕКТИРОВАННУЮ мировую координату в локальную.
        var pose = _physicsWorld.GetPose(bodyHandle);
        System.Numerics.Matrix4x4.Invert(System.Numerics.Matrix4x4.CreateFromQuaternion(pose.Orientation), out var objectToWorld);

        var localHitLocation = System.Numerics.Vector3.Transform(adjustedWorldHitLocation - pose.Position, objectToWorld) + targetObject.LocalCenterOfMass.ToSystemNumerics();

        // Округляем до ближайшего целого, чтобы получить индекс вокселя
        var voxelToRemove = new Vector3i(
            (int)Math.Round(localHitLocation.X),
            (int)Math.Round(localHitLocation.Y),
            (int)Math.Round(localHitLocation.Z)
        );

        // 3. Проверяем, существует ли такой воксель в объекте, и если да, удаляем его.
        if (!targetObject.VoxelCoordinates.Contains(voxelToRemove))
        {
            Console.WriteLine($"[Destroy] Промах! Не найден воксель по координатам {voxelToRemove}");
            return;
        }

        Console.WriteLine($"[Destroy] Уничтожаем воксель {voxelToRemove} в объекте {targetObject.BodyHandle.Value}");
        targetObject.VoxelCoordinates.Remove(voxelToRemove);

        // 4. Уничтожаем старый физический объект.
        _physicsWorld.RemoveBody(targetObject.BodyHandle);
        _voxelObjects.Remove(targetObject);
        targetObject.Dispose();

        // 5. Запускаем анализ связности, чтобы найти "острова" из оставшихся вокселей.
        List<List<Vector3i>> newVoxelIslands = FindConnectedVoxelIslands(targetObject.VoxelCoordinates);
        Console.WriteLine($"[Destroy] Объект раскололся на {newVoxelIslands.Count} частей.");

        // 6. Создаем новые физические объекты для каждого "острова".
        foreach (var island in newVoxelIslands)
        {
            var newObject = new VoxelObject();
            newObject.VoxelCoordinates.AddRange(island);

            // Позиция нового объекта будет такой же, как у старого,
            // но физический движок сам скорректирует ее на основе нового центра масс.
            var handle = _physicsWorld.CreateVoxelObjectBody(newObject.VoxelCoordinates, pose.Position, out var newCenterOfMass);
            newObject.Initialize(handle, newCenterOfMass.ToOpenTK());

            _voxelObjects.Add(newObject);
        }
    }

    /// <summary>
    /// Алгоритм поиска "островов" (связанных групп) вокселей с помощью Поиска в ширину (BFS).
    /// </summary>
    private List<List<Vector3i>> FindConnectedVoxelIslands(List<Vector3i> voxels)
    {
        var islands = new List<List<Vector3i>>();
        var voxelsToVisit = new HashSet<Vector3i>(voxels); // Используем HashSet для быстрого удаления

        while (voxelsToVisit.Count > 0)
        {
            var newIsland = new List<Vector3i>();
            var queue = new Queue<Vector3i>();

            // Начинаем новый "остров" с любого оставшегося вокселя
            queue.Enqueue(voxelsToVisit.First());
            voxelsToVisit.Remove(queue.Peek());

            while (queue.Count > 0)
            {
                var currentVoxel = queue.Dequeue();
                newIsland.Add(currentVoxel);

                // Проверяем 6 соседей (вверх, вниз, влево, вправо, вперед, назад)
                CheckNeighbor(new Vector3i(currentVoxel.X + 1, currentVoxel.Y, currentVoxel.Z));
                CheckNeighbor(new Vector3i(currentVoxel.X - 1, currentVoxel.Y, currentVoxel.Z));
                CheckNeighbor(new Vector3i(currentVoxel.X, currentVoxel.Y + 1, currentVoxel.Z));
                CheckNeighbor(new Vector3i(currentVoxel.X, currentVoxel.Y - 1, currentVoxel.Z));
                CheckNeighbor(new Vector3i(currentVoxel.X, currentVoxel.Y, currentVoxel.Z + 1));
                CheckNeighbor(new Vector3i(currentVoxel.X, currentVoxel.Y, currentVoxel.Z - 1));

                void CheckNeighbor(Vector3i neighbor)
                {
                    if (voxelsToVisit.Contains(neighbor))
                    {
                        voxelsToVisit.Remove(neighbor);
                        queue.Enqueue(neighbor);
                    }
                }
            }
            islands.Add(newIsland);
        }
        return islands;
    }

    /// <summary>
    /// Обновляет состояние всех объектов в чанке, синхронизируя их с физикой.
    /// Вызывается каждый кадр из Game.OnUpdateFrame().
    /// </summary>
    public void Update()
    {
        foreach (var voxelObject in _voxelObjects)
        {
            // Запрашиваем актуальное положение и вращение у физического мира
            var pose = _physicsWorld.GetPose(voxelObject.BodyHandle);
            // И передаем их в рендер-объект
            voxelObject.UpdatePose(pose);
        }
    }

    /// <summary>
    /// Отрисовывает все объекты в чанке.
    /// </summary>
    public void Render(Shader shader, Matrix4 view, Matrix4 projection)
    {
        foreach (var voxelObject in _voxelObjects)
        {
            voxelObject.Render(shader, view, projection);
        }
    }

    /// <summary>
    /// Освобождает ресурсы всех VoxelObject'ов и удаляет их физические тела.
    /// </summary>
    public void Dispose()
    {
        foreach (var obj in _voxelObjects)
        {
            // Перед уничтожением объекта не забываем удалить его тело из симуляции
            _physicsWorld.RemoveBody(obj.BodyHandle);
            obj.Dispose();
        }
        _voxelObjects.Clear();
    }
}