using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepuVector3 = System.Numerics.Vector3;
public class WorldManager : IDisposable
{
// --- Зависимости ---
public PhysicsWorld PhysicsWorld { get; }
private readonly PlayerController _playerController;
// --- Подсистемы (Сервисы) ---
private readonly AsyncChunkGenerator _chunkGenerator;
private readonly AsyncChunkPhysics _physicsBuilder;
private readonly StructuralIntegritySystem _integritySystem;

// --- Хранилище данных ---
private readonly Dictionary<Vector3i, Chunk> _chunks = new();
private readonly Dictionary<BodyHandle, VoxelObject> _bodyToVoxelObjectMap = new();
private readonly Dictionary<StaticHandle, Chunk> _staticToChunkMap = new();
private readonly List<VoxelObject> _voxelObjects = new();

// --- Очереди команд и событий ---
// Данные для создания новых динамических объектов (обломков)
private readonly ConcurrentQueue<VoxelObjectCreationData> _objectsCreationQueue = new();
// Объекты на удаление
private readonly List<VoxelObject> _objectsToRemove = new();

// Очереди для логики видимости (LOD/Culling)
private readonly ConcurrentQueue<List<ChunkGenerationTask>> _incomingChunksToLoad = new();
private readonly ConcurrentQueue<List<Vector3i>> _incomingChunksToUnload = new();

// --- Состояние ---
private readonly object _chunksLock = new();
private readonly HashSet<Vector3i> _chunksInProgress = new();
private readonly HashSet<Vector3i> _activeChunkPositions = new();

private OpenTK.Mathematics.Vector3 _lastChunkUpdatePos = new(float.MaxValue);
private bool _forceUpdate = false;
private volatile bool _isChunkUpdateRunning = false;
private bool _isDisposed = false;

// Оптимизация GC для логики видимости
private readonly HashSet<Vector3i> _requiredChunksSet;
private readonly List<ChunkGenerationTask> _chunksToLoadList;
private readonly List<Vector3i> _chunksToUnloadList;
private readonly List<Vector3i> _sortedLoadPositions;

// --- События ---
public event Action<Chunk> OnChunkLoaded;
public event Action<Chunk> OnChunkModified;
public event Action<Vector3i> OnChunkUnloaded;
public event Action<Vector3i> OnVoxelFastDestroyed;

public const int WorldHeightChunks = 16;
private float _memoryLogTimer = 0f;

// Структура для передачи данных о создании обломка
private struct VoxelObjectCreationData
{
    public List<Vector3i> Voxels;
    public MaterialType Material;
    public System.Numerics.Vector3 WorldPosition;
}

// --- API доступа ---
public Dictionary<Vector3i, Chunk> GetAllChunks() => _chunks;
public List<VoxelObject> GetAllVoxelObjects() => _voxelObjects;

public WorldManager(PhysicsWorld physicsWorld, PlayerController playerController)
{
    PhysicsWorld = physicsWorld;
    _playerController = playerController;

    // Инициализация пулов для расчетов видимости
    _requiredChunksSet = new HashSet<Vector3i>(50000);
    _chunksToLoadList = new List<ChunkGenerationTask>(10000);
    _chunksToUnloadList = new List<Vector3i>(10000);
    _sortedLoadPositions = new List<Vector3i>(50000);

    // Инициализация подсистем
    _chunkGenerator = new AsyncChunkGenerator(12345, GameSettings.GenerationThreads);
    _physicsBuilder = new AsyncChunkPhysics();
    _integritySystem = new StructuralIntegritySystem(this);
}

public void SetGenerationThreadCount(int count) => _chunkGenerator.SetThreadCount(count);

public void Update(float deltaTime)
{
    // 1. Расчет видимости (какие чанки грузить/выгружать)
    UpdateVisibleChunks();

    // 2. Обработка результатов от подсистем
    ProcessGeneratedChunks();
    ProcessPhysicsResults();

    // 3. Обработка динамических объектов
    ProcessNewDebris();
    ProcessVoxelObjects();
    ProcessRemovals();

    // Лог памяти раз в 5 сек
    _memoryLogTimer += deltaTime;
    if (_memoryLogTimer >= 5.0f)
    {
        Console.WriteLine($"[World] Chunks: {_chunks.Count}, Dynamic Objects: {_voxelObjects.Count}");
        _memoryLogTimer = 0f;
    }
}

#region Chunk Management (Visibility Logic)

private void UpdateVisibleChunks()
{
    ApplyChunkUpdates(); // Применяем результаты предыдущего расчета

    if (_isChunkUpdateRunning) return;

    var playerPos = GetPlayerPosition();
    float distSq = (playerPos - _lastChunkUpdatePos).LengthSquared;
    if (distSq < (Chunk.ChunkSize * 2.0f) * (Chunk.ChunkSize * 2.0f) && !_forceUpdate) return;

    _lastChunkUpdatePos = playerPos;
    _forceUpdate = false;

    var pX = (int)Math.Floor(playerPos.X / Chunk.ChunkSize);
    var pZ = (int)Math.Floor(playerPos.Z / Chunk.ChunkSize);
    var currentCenter = new Vector3i(pX, 0, pZ);

    _isChunkUpdateRunning = true;

    HashSet<Vector3i> activeSnapshot;
    lock (_chunksLock) { activeSnapshot = new HashSet<Vector3i>(_activeChunkPositions); }

    int viewDist = GameSettings.RenderDistance;
    int height = WorldHeightChunks;

    // Запускаем расчет в ThreadPool, чтобы не фризить Main Thread
    Task.Run(() =>
    {
        try { CalculateChunksBackground(currentCenter, activeSnapshot, viewDist, height); }
        finally { _isChunkUpdateRunning = false; }
    });
}

// "Математика" видимости. Решает, что добавить, а что удалить.
private void CalculateChunksBackground(Vector3i center, HashSet<Vector3i> activeSnapshot, int viewDist, int height)
{
    _chunksToLoadList.Clear();
    _chunksToUnloadList.Clear();
    _requiredChunksSet.Clear();
    _sortedLoadPositions.Clear();

    int viewDistSq = viewDist * viewDist;

    for (int x = -viewDist; x <= viewDist; x++)
    {
        for (int z = -viewDist; z <= viewDist; z++)
        {
            // УБРАЛИ: if (x * x + z * z > viewDistSq) continue;
            for (int y = 0; y < height; y++) 
                _requiredChunksSet.Add(new Vector3i(center.X + x, y, center.Z + z));
        }
    }

    foreach (var pos in activeSnapshot) if (!_requiredChunksSet.Contains(pos)) _chunksToUnloadList.Add(pos);
    foreach (var pos in _requiredChunksSet) if (!activeSnapshot.Contains(pos)) _sortedLoadPositions.Add(pos);

    // Сортируем загрузку от центра к краям
    _sortedLoadPositions.Sort((a, b) =>
    {
        int dxA = a.X - center.X; int dzA = a.Z - center.Z;
        int distA = dxA * dxA + dzA * dzA;
        int dxB = b.X - center.X; int dzB = b.Z - center.Z;
        int distB = dxB * dxB + dzB * dzB;
        return distA.CompareTo(distB);
    });

    foreach (var pos in _sortedLoadPositions)
    {
        int dx = pos.X - center.X; int dz = pos.Z - center.Z;
        int priority = dx * dx + dz * dz;
        _chunksToLoadList.Add(new ChunkGenerationTask(pos, priority));
    }

    if (_chunksToUnloadList.Count > 0) _incomingChunksToUnload.Enqueue(new List<Vector3i>(_chunksToUnloadList));
    if (_chunksToLoadList.Count > 0) _incomingChunksToLoad.Enqueue(new List<ChunkGenerationTask>(_chunksToLoadList));
}

private void ApplyChunkUpdates()
{
    while (_incomingChunksToUnload.TryDequeue(out var unloadList))
    {
        foreach (var pos in unloadList) UnloadChunk(pos);
    }

    while (_incomingChunksToLoad.TryDequeue(out var loadList))
    {
        foreach (var task in loadList)
        {
            if (!_activeChunkPositions.Contains(task.Position) && !_chunksInProgress.Contains(task.Position))
            {
                _chunksInProgress.Add(task.Position);
                _activeChunkPositions.Add(task.Position);
                // Отправляем задачу в сервис генерации
                _chunkGenerator.EnqueueTask(task.Position, task.Priority);
            }
        }
    }
}

private void UnloadChunk(Vector3i position)
{
    lock (_chunksLock)
    {
        if (_chunks.TryGetValue(position, out var chunk))
        {
            chunk.Dispose();
            _chunks.Remove(position);
            OnChunkUnloaded?.Invoke(position);
        }
    }
    _chunksInProgress.Remove(position);
    _activeChunkPositions.Remove(position);
}

#endregion

#region Pipeline Processing

private void ProcessGeneratedChunks()
{
    // Получаем готовые воксели от генератора
    while (_chunkGenerator.TryGetResult(out var result))
    {
        if (!_activeChunkPositions.Contains(result.Position))
        {
            // Если чанк уже не нужен, возвращаем массив в пул
            System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels);
            continue;
        }

        Chunk chunkToAdd = null;
        lock (_chunksLock)
        {
            if (!_chunks.ContainsKey(result.Position))
            {
                chunkToAdd = new Chunk(result.Position, this);
                chunkToAdd.SetDataFromArray(result.Voxels); // Копирует данные внутрь
                _chunks[result.Position] = chunkToAdd;
            }
        }

        // Массив из генератора больше не нужен, возвращаем в пул
        System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels);

        if (chunkToAdd != null)
        {
            OnChunkLoaded?.Invoke(chunkToAdd);
            // Отправляем чанк на построение физики
            _physicsBuilder.EnqueueTask(chunkToAdd);
        }
    }
}

private void ProcessPhysicsResults()
{
    // Получаем готовые коллайдеры
    while (_physicsBuilder.TryGetResult(out var result))
    {
        if (!result.IsValid) continue;

        using (result.Data) // using вернет массив коллайдеров в пул автоматически
        {
            if (result.TargetChunk == null || !result.TargetChunk.IsLoaded) continue;

            StaticHandle handle = default;
            if (result.Data.Count > 0)
            {
                // Добавляем статику в мир Bepu
                handle = PhysicsWorld.AddStaticChunkBody(
                    (result.TargetChunk.Position * Chunk.ChunkSize).ToSystemNumerics(),
                    result.Data.CollidersArray,
                    result.Data.Count
                );
            }
            // Сообщаем чанку его Handle (для удаления в будущем)
            result.TargetChunk.OnPhysicsRebuilt(handle);
        }
    }
}

public void RebuildPhysics(Chunk chunk)
{
    if (chunk == null || !chunk.IsLoaded) return;
    // Принудительная перестройка (urgent = true)
    _physicsBuilder.EnqueueTask(chunk, urgent: true);
}

#endregion

#region Integrity & Voxel Objects

// Вызывается из StructuralIntegritySystem, когда найден висящий остров
public void CreateDetachedObject(List<Vector3i> globalCluster)
{
    // 1. Удаляем воксели из статического мира
    foreach (var pos in globalCluster) RemoveVoxelGlobal(pos);

    // 2. Считаем границы для локальных координат
    OpenTK.Mathematics.Vector3 min = new OpenTK.Mathematics.Vector3(float.MaxValue);
    foreach (var v in globalCluster)
        min = OpenTK.Mathematics.Vector3.ComponentMin(min, new OpenTK.Mathematics.Vector3(v.X, v.Y, v.Z));

    List<Vector3i> localVoxels = new List<Vector3i>();
    foreach (var v in globalCluster)
        localVoxels.Add(v - new Vector3i((int)min.X, (int)min.Y, (int)min.Z));

    // 3. Ставим в очередь на создание в основном потоке
    _objectsCreationQueue.Enqueue(new VoxelObjectCreationData
    {
        Voxels = localVoxels,
        Material = MaterialType.Stone, // Можно доработать, чтобы брать материал первого блока
        WorldPosition = min.ToSystemNumerics()
    });
}

private void ProcessNewDebris()
{
    while (_objectsCreationQueue.TryDequeue(out var data))
    {
        // Создаем логический объект (без привязки к WorldManager)
        var vo = new VoxelObject(data.Voxels, data.Material);
        
        // Подписываемся на событие "объект опустел"
        vo.OnEmpty += QueueForRemoval;

        // Создаем физическое тело
        // CreateVoxelObjectBody считает центр масс (com) и возвращает его
        var handle = PhysicsWorld.CreateVoxelObjectBody(data.Voxels, data.Material, data.WorldPosition, out var com);
        
        // Корректируем позицию тела с учетом центра масс
        var realPos = data.WorldPosition + com;
        var bodyRef = PhysicsWorld.Simulation.Bodies.GetBodyReference(handle);
        bodyRef.Pose.Position = realPos;

        // Инициализируем связь логики и физики
        vo.InitializePhysics(handle, com.ToOpenTK());

        _voxelObjects.Add(vo);
        RegisterVoxelObject(handle, vo);
    }
}

public void ProcessVoxelObjects()
{
    foreach (var vo in _voxelObjects)
    {
        if (PhysicsWorld.Simulation.Bodies.BodyExists(vo.BodyHandle))
        {
            var pose = PhysicsWorld.GetPose(vo.BodyHandle);
            vo.UpdatePose(pose);
        }
    }
}

public void QueueForRemoval(VoxelObject obj) => _objectsToRemove.Add(obj);

private void ProcessRemovals()
{
    foreach (var obj in _objectsToRemove)
    {
        try
        {
            _bodyToVoxelObjectMap.Remove(obj.BodyHandle);
            _voxelObjects.Remove(obj);
            PhysicsWorld.RemoveBody(obj.BodyHandle);
            obj.Dispose();
        }
        catch { }
    }
    _objectsToRemove.Clear();
}

#endregion

#region Interaction API

public void DestroyVoxelAt(CollidableReference collidable, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
{
    // Сдвигаем точку чуть внутрь блока, чтобы не попасть на границу
    var pointInside = worldHitLocation - worldHitNormal * 0.05f;
    Vector3i globalPos = new Vector3i(
        (int)Math.Floor(pointInside.X),
        (int)Math.Floor(pointInside.Y),
        (int)Math.Floor(pointInside.Z));

    // А. Разрушение СТАТИКИ
    if (collidable.Mobility == CollidableMobility.Static)
    {
        // Пытаемся удалить воксель из чанка
        if (RemoveVoxelGlobal(globalPos))
        {
            // Если успешно удалили:
            NotifyVoxelFastDestroyed(globalPos);
            // Запускаем проверку на висящие острова (асинхронно)
            _integritySystem.QueueCheck(globalPos);
        }
    }
    // Б. Разрушение ДИНАМИКИ (VoxelObject)
    else if (collidable.Mobility == CollidableMobility.Dynamic &&
             _bodyToVoxelObjectMap.TryGetValue(collidable.BodyHandle, out var voxelObj))
    {
        // Переводим мировую точку в локальную систему координат объекта
        Matrix4 model = Matrix4.CreateTranslation(-voxelObj.LocalCenterOfMass) *
                        Matrix4.CreateFromQuaternion(voxelObj.Rotation) *
                        Matrix4.CreateTranslation(voxelObj.Position);
        Matrix4 invModel = Matrix4.Invert(model);

        Vector4 localHit4 = new Vector4(pointInside.ToOpenTK(), 1.0f) * invModel;
        Vector3i localVoxel = new Vector3i(
            (int)Math.Floor(localHit4.X),
            (int)Math.Floor(localHit4.Y),
            (int)Math.Floor(localHit4.Z));

        if (voxelObj.RemoveVoxel(localVoxel))
        {
            voxelObj.RebuildMeshAndPhysics(PhysicsWorld);
        }
    }
}

private bool RemoveVoxelGlobal(Vector3i globalPos)
{
    Vector3i chunkPos = GetChunkPosFromGlobal(globalPos);
    if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded)
    {
        Vector3i localPos = globalPos - (chunkPos * Chunk.ChunkSize);
        return chunk.RemoveVoxelAndUpdate(localPos);
    }
    return false;
}

// --- Helpers ---

private Vector3i GetChunkPosFromGlobal(Vector3i p) => new Vector3i(
    (int)Math.Floor((float)p.X / Chunk.ChunkSize),
    (int)Math.Floor((float)p.Y / Chunk.ChunkSize),
    (int)Math.Floor((float)p.Z / Chunk.ChunkSize));

public bool IsVoxelSolidGlobal(Vector3i globalPos)
{
    var chunkPos = GetChunkPosFromGlobal(globalPos);
    if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded)
    {
        var local = globalPos - chunkPos * Chunk.ChunkSize;
        return chunk.IsVoxelSolidAt(local);
    }
    return false;
}

public bool IsChunkLoadedAt(Vector3i globalPos)
{
    var chunkPos = GetChunkPosFromGlobal(globalPos);
    return _chunks.ContainsKey(chunkPos) && _chunks[chunkPos].IsLoaded;
}

public void NotifyVoxelFastDestroyed(Vector3i worldPos) => OnVoxelFastDestroyed?.Invoke(worldPos);
public void NotifyChunkModified(Chunk chunk) => OnChunkModified?.Invoke(chunk);

public void RegisterVoxelObject(BodyHandle handle, VoxelObject obj)
{
    lock (_chunksLock) _bodyToVoxelObjectMap[handle] = obj;
}

public void RegisterChunkStatic(StaticHandle handle, Chunk chunk)
{
    lock (_chunksLock) _staticToChunkMap[handle] = chunk;
}

public void UnregisterChunkStatic(StaticHandle handle)
{
    lock (_chunksLock) _staticToChunkMap.Remove(handle);
}

public OpenTK.Mathematics.Vector3 GetPlayerPosition() => 
    PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle).Pose.Position.ToOpenTK();

public List<Chunk> GetChunksSnapshot()
{
    lock (_chunksLock) return new List<Chunk>(_chunks.Values);
}

public int GetViewRangeInVoxels() => GameSettings.RenderDistance * Chunk.ChunkSize;

#endregion

public void Dispose()
{
    _isDisposed = true;

    // Останавливаем сервисы
    _chunkGenerator.Dispose();
    _physicsBuilder.Dispose();
    _integritySystem.Dispose();

    lock (_chunksLock)
    {
        foreach (var chunk in _chunks.Values) chunk.Dispose();
        _chunks.Clear();
    }
    
    foreach(var vo in _voxelObjects) vo.Dispose();
    _voxelObjects.Clear();
}
}