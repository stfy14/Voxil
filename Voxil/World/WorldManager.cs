// /World/WorldManager.cs
using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;
using BepuVector3 = System.Numerics.Vector3;

public class WorldManager : IDisposable
{
    public PhysicsWorld PhysicsWorld { get; }
    private readonly PlayerController _playerController;

    // --- НАСТРОЙКА ---
    // Вы можете изменить это значение, чтобы увеличить или уменьшить дальность прорисовки.
    private int _viewDistance = 8;

    // Словарь _chunks теперь наша база данных мира.
    // Он хранит ВСЕ когда-либо сгенерированные чанки, даже если они выгружены.
    private readonly Dictionary<Vector3i, Chunk> _chunks = new();

    private readonly Dictionary<BodyHandle, VoxelObject> _bodyToVoxelObjectMap = new();
    private readonly Dictionary<StaticHandle, Chunk> _staticToChunkMap = new();
    private readonly IWorldGenerator _generator;
    private readonly List<VoxelObject> _objectsToRemove = new();

    // --- Новая система управления чанками ---
    private Vector3i _lastPlayerChunkPosition = new Vector3i(int.MaxValue); // Инициализируем невозможным значением, чтобы запустить первую проверку
    private readonly Queue<Vector3i> _chunksToProcessQueue = new();
    private readonly HashSet<Vector3i> _activeChunkPositions = new HashSet<Vector3i>();


    public WorldManager(PhysicsWorld physicsWorld, PlayerController playerController)
    {
        PhysicsWorld = physicsWorld;
        _playerController = playerController;
        _generator = new PerlinGenerator(12345);
    }

    private float _memoryLogTimer = 0f; //Дебаг 
    public void Update(float deltaTime)
    {
        UpdateVisibleChunkList();
        ProcessChunkQueue();

        foreach (var chunk in _chunks.Values)
        {
            chunk.Update(deltaTime);
        }

        ProcessRemovals();

        _memoryLogTimer += deltaTime;
        if (_memoryLogTimer >= 5.0f)
        {
            var loadedCount = _chunks.Values.Count(c => c._isLoaded); // Потребует сделать _isLoaded public ПОТОМ УБРАТЬ НА PRIVATE
            var totalCount = _chunks.Count;
            var memoryMB = GC.GetTotalMemory(false) / (1024 * 1024);

            Console.WriteLine($"[WorldManager] Чанков загружено: {loadedCount}/{totalCount}, Память: {memoryMB} MB");
            _memoryLogTimer = 0f;
        }
    }

    private void UpdateVisibleChunkList()
    {
        var playerPosition = PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle).Pose.Position;
        var playerChunkPos = new Vector3i(
            (int)Math.Floor(playerPosition.X / Chunk.ChunkSize),
            0, // Y-координата чанка пока не важна
            (int)Math.Floor(playerPosition.Z / Chunk.ChunkSize)
        );

        if (playerChunkPos == _lastPlayerChunkPosition) return;

        _lastPlayerChunkPosition = playerChunkPos;
        Console.WriteLine($"[WorldManager] Игрок вошел в чанк: {playerChunkPos}. Обновление списка активных чанков.");

        var previouslyActiveChunks = new HashSet<Vector3i>(_activeChunkPositions);
        _activeChunkPositions.Clear();

        for (int x = -_viewDistance; x <= _viewDistance; x++)
        {
            for (int z = -_viewDistance; z <= _viewDistance; z++)
            {
                var chunkPos = new Vector3i(playerChunkPos.X + x, 0, playerChunkPos.Z + z);
                _activeChunkPositions.Add(chunkPos);

                // Если этот чанк не был активен в прошлый раз, добавляем его в очередь на обработку
                if (!previouslyActiveChunks.Contains(chunkPos))
                {
                    _chunksToProcessQueue.Enqueue(chunkPos);
                }
            }
        }

        // Находим чанки, которые больше не активны, и добавляем их в очередь на выгрузку
        foreach (var oldChunkPos in previouslyActiveChunks)
        {
            if (!_activeChunkPositions.Contains(oldChunkPos))
            {
                _chunksToProcessQueue.Enqueue(oldChunkPos);
            }
        }
    }

    private void ProcessChunkQueue()
    {
        if (_chunksToProcessQueue.Count == 0) return;

        var chunkPos = _chunksToProcessQueue.Dequeue();

        // Решаем, нужно ли загружать или выгружать этот чанк
        bool shouldBeActive = _activeChunkPositions.Contains(chunkPos);

        _chunks.TryGetValue(chunkPos, out var chunk);

        if (shouldBeActive)
        {
            // Если чанк должен быть активен, но его нет, создаем и генерируем
            if (chunk == null)
            {
                var newChunk = new Chunk(chunkPos, this);
                _chunks.Add(chunkPos, newChunk);
                newChunk.Generate(_generator); // Generate сразу вызывает Load
            }
            else // Если он есть, но был выгружен, загружаем
            {
                chunk.Load();
            }
        }
        else
        {
            // Если чанк не должен быть активен, но он существует и загружен, выгружаем
            if (chunk != null)
            {
                chunk.Unload();
            }
        }
    }

    public void RegisterChunkStatic(StaticHandle handle, Chunk chunk)
    {
        _staticToChunkMap[handle] = chunk;
    }

    private void RegisterVoxelObjectBody(BodyHandle handle, VoxelObject voxelObject)
    {
        _bodyToVoxelObjectMap[handle] = voxelObject;
    }

    public void QueueForRemoval(VoxelObject obj)
    {
        if (obj != null && !_objectsToRemove.Contains(obj))
            _objectsToRemove.Add(obj);
    }

    private void ProcessRemovals()
    {
        if (_objectsToRemove.Count == 0) return;

        foreach (var obj in _objectsToRemove)
        {
            if (obj == null) continue;
            _bodyToVoxelObjectMap.Remove(obj.BodyHandle);
            PhysicsWorld.RemoveBody(obj.BodyHandle);
            // TODO: Улучшить логику поиска чанка, в котором находится объект
            if (_chunks.TryGetValue(Vector3i.Zero, out var chunk))
            {
                chunk.RemoveVoxelObject(obj);
            }
            obj.Dispose();
        }
        _objectsToRemove.Clear();
    }

    public void DestroyVoxelAt(CollidableReference collidable, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        if (collidable.Mobility == CollidableMobility.Dynamic)
        {
            if (_bodyToVoxelObjectMap.TryGetValue(collidable.BodyHandle, out var dynamicObject))
            {
                DestroyDynamicVoxelAt(dynamicObject, worldHitLocation, worldHitNormal);
            }
        }
        else
        {
            if (_staticToChunkMap.TryGetValue(collidable.StaticHandle, out var chunk))
            {
                DestroyStaticVoxelAt(chunk, worldHitLocation, worldHitNormal);
            }
        }
    }

    private void DestroyStaticVoxelAt(Chunk chunk, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        var chunkWorldPos = (chunk.Position * Chunk.ChunkSize).ToSystemNumerics();
        var localHitLocation = worldHitLocation - chunkWorldPos - worldHitNormal * 0.001f;

        var voxelToRemove = new Vector3i(
            (int)Math.Floor(localHitLocation.X + 0.5f),
            (int)Math.Floor(localHitLocation.Y + 0.5f),
            (int)Math.Floor(localHitLocation.Z + 0.5f));

        // ИЗМЕНЕНИЕ: Используем метод, который сразу обновляет чанк
        if (chunk.RemoveVoxelAndUpdate(voxelToRemove))
        {
            CheckForDetachedVoxelGroups(chunk, voxelToRemove);
        }
    }

    private VoxelObject _createAndAddVoxelObject(List<Vector3i> localVoxelCoordinates, MaterialType material, BepuVector3 worldPosition)
    {
        if (localVoxelCoordinates == null || localVoxelCoordinates.Count == 0) return null;

        var newObject = new VoxelObject(localVoxelCoordinates, material);
        var handle = PhysicsWorld.CreateVoxelObjectBody(localVoxelCoordinates, material, worldPosition, out var newCenterOfMass);

        if (!PhysicsWorld.Simulation.Bodies.BodyExists(handle)) return null;

        newObject.InitializePhysics(handle, newCenterOfMass.ToOpenTK());
        newObject.BuildMesh();

        // TODO: Логика добавления в правильный чанк
        if (_chunks.TryGetValue(Vector3i.Zero, out var chunk))
        {
            chunk.AddVoxelObject(newObject);
        }
        RegisterVoxelObjectBody(handle, newObject);

        return newObject;
    }

    private void DestroyDynamicVoxelAt(VoxelObject targetObject, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        var pose = PhysicsWorld.GetPose(targetObject.BodyHandle);
        var invOrientation = System.Numerics.Quaternion.Inverse(pose.Orientation);
        var localHitLocation = BepuVector3.Transform(worldHitLocation - pose.Position, invOrientation) + targetObject.LocalCenterOfMass.ToSystemNumerics();
        var localNormal = BepuVector3.Transform(worldHitNormal, invOrientation);
        localHitLocation -= localNormal * 0.001f;

        var voxelToRemove = new Vector3i(
            (int)Math.Floor(localHitLocation.X + 0.5f),
            (int)Math.Floor(localHitLocation.Y + 0.5f),
            (int)Math.Floor(localHitLocation.Z + 0.5f));

        if (!targetObject.VoxelCoordinates.Contains(voxelToRemove)) return;

        var remainingVoxels = new List<Vector3i>(targetObject.VoxelCoordinates);
        remainingVoxels.Remove(voxelToRemove);

        if (remainingVoxels.Count == 0)
        {
            QueueForRemoval(targetObject);
            return;
        }

        List<List<Vector3i>> newVoxelIslands = FindConnectedVoxelIslands(remainingVoxels);

        if (newVoxelIslands.Count == 1)
        {
            targetObject.VoxelCoordinates.Remove(voxelToRemove);
            targetObject.RebuildMeshAndPhysics(this.PhysicsWorld);
        }
        else
        {
            var originalMaterial = targetObject.Material;
            var originalPose = pose;

            foreach (var island in newVoxelIslands)
            {
                var localIslandCenter = BepuVector3.Zero;
                foreach (var voxel in island)
                    localIslandCenter += new BepuVector3(voxel.X, voxel.Y, voxel.Z);
                localIslandCenter /= island.Count;

                var offsetFromOldCoM = localIslandCenter - targetObject.LocalCenterOfMass.ToSystemNumerics();
                var rotatedOffset = BepuVector3.Transform(offsetFromOldCoM, originalPose.Orientation);
                var newWorldPosition = originalPose.Position + rotatedOffset;

                _createAndAddVoxelObject(island, originalMaterial, newWorldPosition);
            }
            QueueForRemoval(targetObject);
        }
    }

    private List<List<Vector3i>> FindConnectedVoxelIslands(List<Vector3i> voxels)
    {
        var islands = new List<List<Vector3i>>();
        var voxelsToVisit = new HashSet<Vector3i>(voxels);

        while (voxelsToVisit.Count > 0)
        {
            var newIsland = new List<Vector3i>();
            var queue = new Queue<Vector3i>();
            queue.Enqueue(voxelsToVisit.First());
            voxelsToVisit.Remove(queue.Peek());

            while (queue.Count > 0)
            {
                var currentVoxel = queue.Dequeue();
                newIsland.Add(currentVoxel);

                var neighbors = new Vector3i[]
                {
                    currentVoxel + new Vector3i(1,0,0), currentVoxel + new Vector3i(-1,0,0),
                    currentVoxel + new Vector3i(0,1,0), currentVoxel + new Vector3i(0,-1,0),
                    currentVoxel + new Vector3i(0,0,1), currentVoxel + new Vector3i(0,0,-1)
                };

                foreach (var neighbor in neighbors)
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

    private void CheckForDetachedVoxelGroups(Chunk sourceChunk, Vector3i removedVoxelLocalPos)
{
    var directions = new[]
    {
        new Vector3i(1, 0, 0), new Vector3i(-1, 0, 0),
        new Vector3i(0, 1, 0), new Vector3i(0, -1, 0),
        new Vector3i(0, 0, 1), new Vector3i(0, 0, -1)
    };

    // Проверяем каждого соседа удаленного вокселя
    foreach (var direction in directions)
    {
        var neighborPos = removedVoxelLocalPos + direction;

        // Получаем доступ к вокселям чанка. Для этого нужно сделать поле _voxels в Chunk "internal" или добавить public get-тер
        // В файле Chunk.cs измените: private readonly HashSet<Vector3i> _voxels -> public readonly HashSet<Vector3i> Voxels { get; }
        if (!sourceChunk.Voxels.Contains(neighborPos)) continue;

        var visited = new HashSet<Vector3i>();
        var queue = new Queue<Vector3i>();
        bool isGrounded = false;
        
        // Лимит поиска, чтобы избежать лагов при разрушении огромных структур
        const int searchLimit = 2000; 

        queue.Enqueue(neighborPos);
        visited.Add(neighborPos);

        while (queue.Count > 0)
        {
            if (visited.Count > searchLimit)
            {
                // Если мы проверили слишком много блоков, скорее всего, это часть большой структуры.
                // Прерываем поиск для производительности.
                isGrounded = true; 
                break;
            }

            var currentPos = queue.Dequeue();

            // Проверка "заземления": если воксель на границе чанка, считаем его устойчивым.
            if (currentPos.X == 0 || currentPos.X == Chunk.ChunkSize - 1 ||
                currentPos.Y == 0 || // Y=0 - это всегда земля
                currentPos.Z == 0 || currentPos.Z == Chunk.ChunkSize - 1)
            {
                isGrounded = true;
                break; // Нашли связь с миром, эта группа не отвалится
            }

            // Добавляем соседей в очередь
            foreach (var dir in directions)
            {
                var nextPos = currentPos + dir;
                if (sourceChunk.Voxels.Contains(nextPos) && !visited.Contains(nextPos))
                {
                    visited.Add(nextPos);
                    queue.Enqueue(nextPos);
                }
            }
        }

        // Если поиск завершился, и мы не нашли "землю", значит это плавающий остров
        if (!isGrounded)
        {
            Console.WriteLine($"[WorldManager] Найдена отсоединенная группа из {visited.Count} вокселей.");
            
            // Создаем список вокселей для нового объекта.
            // Координаты должны быть относительными, поэтому найдем "минимальный угол" острова.
            var minCorner = new Vector3i(int.MaxValue);
            foreach (var pos in visited)
            {
                minCorner.X = Math.Min(minCorner.X, pos.X);
                minCorner.Y = Math.Min(minCorner.Y, pos.Y);
                minCorner.Z = Math.Min(minCorner.Z, pos.Z);
            }

            var newObjectVoxels = new List<Vector3i>();
            foreach (var pos in visited)
            {
                newObjectVoxels.Add(pos - minCorner);
            }

            bool removedAny = false;
            foreach (var pos in visited)
            {
                if (sourceChunk.RemoveVoxelAt(pos))
                {
                    removedAny = true;
                }
            }

            // Шаг 2: Если что-то было удалено, ОДИН РАЗ финализируем изменения
            if (removedAny)
            {
                sourceChunk.FinalizeGroupRemoval();
            }

            // --- КОНЕЦ ИЗМЕНЕНИЙ ---

            var chunkWorldPos = (sourceChunk.Position * Chunk.ChunkSize).ToSystemNumerics();
            var newObjectWorldPos = chunkWorldPos + new BepuVector3(minCorner.X, minCorner.Y, minCorner.Z);

            _createAndAddVoxelObject(newObjectVoxels, sourceChunk.Material, newObjectWorldPos);

            return;
        }
    }
}

    public void Render(Shader shader, Matrix4 view, Matrix4 projection)
    {
        foreach (var chunk in _chunks.Values)
        {
            chunk.Render(shader, view, projection);
        }
    }

    public void Dispose()
    {
        foreach (var vo in _bodyToVoxelObjectMap.Values)
        {
            QueueForRemoval(vo);
        }
        ProcessRemovals();

        foreach (var chunk in _chunks.Values)
        {
            chunk.Dispose();
        }
        _chunks.Clear();
        _bodyToVoxelObjectMap.Clear();
        _staticToChunkMap.Clear();
    }
}