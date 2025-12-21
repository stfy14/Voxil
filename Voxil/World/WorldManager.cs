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
using System.Buffers;
using BepuVector3 = System.Numerics.Vector3;

#region Task Structures
public readonly struct ChunkGenerationTask
{
    public readonly Vector3i Position;
    public readonly int Priority;

    public ChunkGenerationTask(Vector3i pos, int priority)
    {
        Position = pos;
        Priority = priority;
    }
}
public readonly struct ChunkGenerationResult
{
    public readonly Vector3i Position;
    public readonly MaterialType[] Voxels;

    // Конструктор обязателен!
    public ChunkGenerationResult(Vector3i pos, MaterialType[] voxels)
    {
        Position = pos;
        Voxels = voxels;
    }
}
public readonly struct PhysicsBuildTask
{
    public readonly Chunk ChunkToProcess;
    
    // Проверка на "пустоту" структуры (аналог null)
    public bool IsValid => ChunkToProcess != null;

    public PhysicsBuildTask(Chunk chunk)
    {
        ChunkToProcess = chunk;
    }
}
public readonly struct PhysicsBuildResult
{
    public readonly Chunk TargetChunk;
    public readonly PhysicsBuildResultData Data;
    public readonly bool IsValid;

    public PhysicsBuildResult(Chunk chunk, PhysicsBuildResultData data)
    {
        TargetChunk = chunk;
        Data = data;
        IsValid = chunk != null;
    }
}
public class DetachmentCheckTask { public Chunk Chunk; public Vector3i RemovedVoxelLocalPos; }
#endregion

public class WorldManager : IDisposable
{
    public PhysicsWorld PhysicsWorld { get; }
    private readonly PlayerController _playerController;
    private readonly IWorldGenerator _generator;

    // Хранилище чанков
    private readonly Dictionary<Vector3i, Chunk> _chunks = new();
    private readonly Dictionary<BodyHandle, VoxelObject> _bodyToVoxelObjectMap = new();
    private readonly Dictionary<StaticHandle, Chunk> _staticToChunkMap = new();

    private readonly List<VoxelObject> _voxelObjects = new();
    private readonly ConcurrentQueue<VoxelObject> _voxelObjectsToAdd = new();
    private readonly List<VoxelObject> _objectsToRemove = new();

    // Потоки
    private List<Thread> _generationThreads = new List<Thread>();
    private CancellationTokenSource _genCts = new CancellationTokenSource();
    
    private readonly Thread _detachmentThread;
    private readonly Thread _physicsBuilderThread;
    
    private volatile bool _isDisposed = false;

    // Очереди
    private readonly BlockingCollection<ChunkGenerationTask> _generationQueue = new(new ConcurrentQueue<ChunkGenerationTask>());
    private readonly ConcurrentQueue<ChunkGenerationResult> _generatedChunksQueue = new();
    
    private readonly BlockingCollection<PhysicsBuildTask> _physicsBuildQueue = new(new ConcurrentQueue<PhysicsBuildTask>());
    private readonly ConcurrentQueue<PhysicsBuildResult> _physicsResultQueue = new ConcurrentQueue<PhysicsBuildResult>();
    
    private readonly BlockingCollection<DetachmentCheckTask> _detachmentQueue = new(new ConcurrentQueue<DetachmentCheckTask>());
    private readonly ConcurrentQueue<PhysicsBuildTask> _urgentPhysicsQueue = new();

    // Синхронизация
    private readonly object _chunksLock = new();
    private readonly HashSet<Vector3i> _chunksInProgress = new();
    private readonly HashSet<Vector3i> _activeChunkPositions = new();
    
    private Vector3i _lastPlayerChunkPosition = new(int.MaxValue);
    
    private OpenTK.Mathematics.Vector3 _lastChunkUpdatePos = new(float.MaxValue);
    private bool _forceUpdate = false; 

    // --- АСИНХРОННОСТЬ ---
    private volatile bool _isChunkUpdateRunning = false;
    private readonly ConcurrentQueue<List<ChunkGenerationTask>> _incomingChunksToLoad = new();
    private readonly ConcurrentQueue<List<Vector3i>> _incomingChunksToUnload = new();
    
    // --- ОПТИМИЗАЦИЯ GC: Переиспользуемые коллекции ---
    // Выносим коллекции из CalculateChunksBackground в поля класса.
    // Инициализируем их один раз в конструкторе.
    private readonly HashSet<Vector3i> _requiredChunksSet;
    private readonly List<ChunkGenerationTask> _chunksToLoadList;
    private readonly List<Vector3i> _chunksToUnloadList;
    private readonly List<Vector3i> _sortedLoadPositions; // Для сортировки тоже нужен буфер

    // --- СОБЫТИЯ ---
    public event Action<Chunk> OnChunkLoaded;     
    public event Action<Chunk> OnChunkModified;   
    public event Action<Vector3i> OnChunkUnloaded; 
    public event Action<Vector3i> OnVoxelFastDestroyed;

    // --- КОНСТАНТЫ ---
    public const int WorldHeightChunks = 16; 

    private float _memoryLogTimer = 0f;

    public Dictionary<Vector3i, Chunk> GetAllChunks() => _chunks;
    public List<VoxelObject> GetAllVoxelObjects() => _voxelObjects;

    public WorldManager(PhysicsWorld physicsWorld, PlayerController playerController)
    {
        PhysicsWorld = physicsWorld;
        _playerController = playerController;
        _generator = new PerlinGenerator(12345);

        // --- Инициализация переиспользуемых коллекций ---
        // Выделяем память один раз при старте с запасом
        _requiredChunksSet = new HashSet<Vector3i>(50000); 
        _chunksToLoadList = new List<ChunkGenerationTask>(10000);
        _chunksToUnloadList = new List<Vector3i>(10000);
        _sortedLoadPositions = new List<Vector3i>(50000);

        SetGenerationThreadCount(GameSettings.GenerationThreads);

        _detachmentThread = new Thread(DetachmentThreadLoop) 
            { IsBackground = true, Name = "DetachmentCheck", Priority = ThreadPriority.Lowest };
        
        _physicsBuilderThread = new Thread(PhysicsBuilderThreadLoop) 
            { IsBackground = true, Name = "PhysicsBuilder", Priority = ThreadPriority.BelowNormal };

        _detachmentThread.Start();
        _physicsBuilderThread.Start();
    }

    public void SetGenerationThreadCount(int count)
    {
        if (_generationThreads.Count == count) return;

        _genCts.Cancel();
        foreach (var t in _generationThreads) if (t.IsAlive) t.Join(10); 
        _generationThreads.Clear();
        _genCts = new CancellationTokenSource();

        for (int i = 0; i < count; i++)
        {
            var t = new Thread(() => GenerationThreadLoop(_genCts.Token))
            {
                IsBackground = true, Priority = ThreadPriority.Lowest, Name = $"GenThread_{i}"
            };
            t.Start();
            _generationThreads.Add(t);
        }
        Console.WriteLine($"[World] Generation threads set to: {count}");
    }

    #region Thread Loops

    private void GenerationThreadLoop(CancellationToken token)
    {
        var stopwatch = new Stopwatch();
        while (!_isDisposed && !token.IsCancellationRequested)
        {
            try
            {
                // Берем задачу из очереди (структура)
                if (_generationQueue.TryTake(out var task, 100, token))
                {
                    // --- ЗАМЕР НАЧАЛСЯ ---
                    if (PerformanceMonitor.IsEnabled) stopwatch.Restart();

                    // Арендуем массив
                    var voxels = System.Buffers.ArrayPool<MaterialType>.Shared.Rent(Chunk.Volume);
                    
                    // Генерируем
                    _generator.GenerateChunk(task.Position, voxels);

                    // --- ЗАМЕР ЗАКОНЧИЛСЯ ---
                    if (PerformanceMonitor.IsEnabled)
                    {
                        stopwatch.Stop();
                        PerformanceMonitor.Record(ThreadType.Generation, stopwatch.ElapsedTicks);
                    }

                    // Отправляем результат (структура)
                    _generatedChunksQueue.Enqueue(new ChunkGenerationResult(task.Position, voxels));
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[GenerationThread] Error: {ex.Message}"); }
        }
    }

    public void RebuildPhysics(Chunk chunk)
    {
        if (chunk == null || !chunk.IsLoaded) return;
        _urgentPhysicsQueue.Enqueue(new PhysicsBuildTask(chunk));
    }

    private void PhysicsBuilderThreadLoop()
    {
        while (!_isDisposed)
        {
            try
            {
                // Структуры не могут быть null, используем default
                PhysicsBuildTask task = default;
                bool gotTask = false;

                if (_urgentPhysicsQueue.TryDequeue(out task)) gotTask = true;
                else if (_physicsBuildQueue.TryTake(out task, 10)) gotTask = true;

                // Проверяем IsValid вместо null
                if (gotTask && task.IsValid)
                {
                    if (task.ChunkToProcess == null || !task.ChunkToProcess.IsLoaded) continue;

                    var rawVoxels = System.Buffers.ArrayPool<MaterialType>.Shared.Rent(Chunk.Volume);
                    
                    // ... (копирование данных) ...
                    var src = task.ChunkToProcess.GetVoxelsUnsafe();
                    var rwLock = task.ChunkToProcess.GetLock();
                    rwLock.EnterReadLock();
                    try 
                    { 
                        Buffer.BlockCopy(src, 0, rawVoxels, 0, Chunk.Volume);
                    }
                    finally { rwLock.ExitReadLock(); }

                    var resultData = VoxelPhysicsBuilder.GenerateColliders(rawVoxels, task.ChunkToProcess.Position);
                    
                    System.Buffers.ArrayPool<MaterialType>.Shared.Return(rawVoxels);

                    _physicsResultQueue.Enqueue(new PhysicsBuildResult(task.ChunkToProcess, resultData));
                }
            }
            catch (Exception ex) { Console.WriteLine($"[PhysicsBuilderThread] Error: {ex.Message}"); }
        }
    }

    private void DetachmentThreadLoop()
    {
        while (!_isDisposed)
        {
            if (_detachmentQueue.TryTake(out var task, 50)) { /* Логика отваливающихся кусков */ }
        }
    }

    #endregion

    #region Main Update

    public void Update(float deltaTime)
    {
        UpdateVisibleChunks();
        ProcessGeneratedChunks();
        ProcessPhysicsResults();
        ProcessVoxelObjects();
        ProcessRemovals();

        _memoryLogTimer += deltaTime;
        if (_memoryLogTimer >= 5.0f)
        {
            Console.WriteLine($"[World] Loaded Chunks: {_chunks.Count}");
            _memoryLogTimer = 0f;
        }
    }

    private void UpdateVisibleChunks()
    {
        ApplyChunkUpdates();
        if (_isChunkUpdateRunning) return;

        // Получаем позицию игрока
        var playerPos = GetPlayerPosition(); // Или берем из физики напрямую

        // ОПТИМИЗАЦИЯ: Проверяем, сдвинулся ли игрок достаточно далеко
        // 32.0f = 2 чанка (16 * 2). Обновляем список задач только если прошли это расстояние.
        float distSq = (playerPos - _lastChunkUpdatePos).LengthSquared;
        if (distSq < (Chunk.ChunkSize * 2.0f) * (Chunk.ChunkSize * 2.0f) && !_forceUpdate) 
        {
            return; 
        }

        // Обновляем позицию последнего апдейта
        _lastChunkUpdatePos = playerPos;
        _forceUpdate = false;

        // Логика расчета центра чанка
        var pX = (int)Math.Floor(playerPos.X / Chunk.ChunkSize);
        var pZ = (int)Math.Floor(playerPos.Z / Chunk.ChunkSize);
        var currentCenter = new Vector3i(pX, 0, pZ);

        _isChunkUpdateRunning = true;

        HashSet<Vector3i> activeSnapshot;
        lock (_chunksLock) { activeSnapshot = new HashSet<Vector3i>(_activeChunkPositions); }
        
        int viewDist = GameSettings.RenderDistance;
        int height = WorldHeightChunks;
        
        Task.Run(() => 
        {
            try { CalculateChunksBackground(currentCenter, activeSnapshot, viewDist, height); }
            finally { _isChunkUpdateRunning = false; }
        });
    }

private void CalculateChunksBackground(Vector3i center, HashSet<Vector3i> activeSnapshot, int viewDist, int height)
    {
        // Вместо создания 'new' - чистим существующие
        _chunksToLoadList.Clear();
        _chunksToUnloadList.Clear();
        _requiredChunksSet.Clear();
        _sortedLoadPositions.Clear();

        int viewDistSq = viewDist * viewDist;
        
        // 1. Заполняем _requiredChunksSet (вместо локальной переменной)
        for (int x = -viewDist; x <= viewDist; x++)
        {
            for (int z = -viewDist; z <= viewDist; z++)
            {
                if (x*x + z*z > viewDistSq) continue;
                for (int y = 0; y < height; y++) _requiredChunksSet.Add(new Vector3i(center.X + x, y, center.Z + z));
            }
        }

        // 2. Ищем, что выгрузить
        foreach (var pos in activeSnapshot) if (!_requiredChunksSet.Contains(pos)) _chunksToUnloadList.Add(pos);

        // 3. Ищем, что загрузить (используем _sortedLoadPositions как временный буфер)
        foreach (var pos in _requiredChunksSet) if (!activeSnapshot.Contains(pos)) _sortedLoadPositions.Add(pos);

        // 4. Сортируем
        _sortedLoadPositions.Sort((a, b) => 
        {
            int dxA = a.X - center.X; int dzA = a.Z - center.Z;
            int distA = dxA*dxA + dzA*dzA;
            int dxB = b.X - center.X; int dzB = b.Z - center.Z;
            int distB = dxB*dxB + dzB*dzB;
            // Упростим приоритет для ясности
            return distA.CompareTo(distB);
        });

        // 5. Формируем финальный список задач на загрузку
        foreach(var pos in _sortedLoadPositions)
        {
            int dx = pos.X - center.X; int dz = pos.Z - center.Z;
            int priority = dx*dx + dz*dz;
            _chunksToLoadList.Add(new ChunkGenerationTask(pos, priority));
        }

        // 6. Передаем в очереди (копируем, чтобы не было гонки потоков)
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
                    _generationQueue.Add(task);
                }
            }
        }
    }

    private void ProcessGeneratedChunks()
    {
        // БЮДЖЕТ ВРЕМЕНИ: 1.5 мс.
        // Если не успели обработать все чанки из очереди - откладываем на следующий кадр.
        // Это спасет от лагов при телепортации или быстром полете.
        long startTime = Stopwatch.GetTimestamp();
        long maxTicks = (long)(0.0015 * Stopwatch.Frequency); 

        // Обрабатываем, пока есть задачи И есть время
        while (_generatedChunksQueue.TryDequeue(out var result))
        {
            // 1. Проверка актуальности (без блокировки)
            // Если чанк уже не нужен игроку - просто возвращаем массив в пул
            if (!_activeChunkPositions.Contains(result.Position)) 
            {
                 System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels);
                 continue;
            }

            Chunk chunkToAdd = null;
            bool added = false;

            // 2. Создаем чанк (ТЯЖЕЛАЯ ОПЕРАЦИЯ - делаем ДО блокировки, если возможно)
            // Но Chunk требует ссылку на WorldManager, так что ок.
            
            // 3. БЛОКИРОВКА (Минимальная по времени)
            lock (_chunksLock)
            {
                // Двойная проверка внутри лока
                if (!_chunks.ContainsKey(result.Position))
                {
                    chunkToAdd = new Chunk(result.Position, this);
                    // Копируем данные (быстро, через Buffer.BlockCopy)
                    chunkToAdd.SetDataFromArray(result.Voxels);
                    
                    _chunks[result.Position] = chunkToAdd;
                    added = true;
                }
            }

            // 4. Пост-обработка (БЕЗ БЛОКИРОВКИ)
            if (added && chunkToAdd != null)
            {
                // Сначала уведомляем рендер, чтобы игрок СРАЗУ увидел чанк
                OnChunkLoaded?.Invoke(chunkToAdd);

                // Потом ставим в очередь на физику (пусть считается в фоне)
                _physicsBuildQueue.Add(new PhysicsBuildTask(chunkToAdd));
            }
            
            // 5. Возвращаем массив (обязательно!)
            System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels);

            // Проверка времени
            if (Stopwatch.GetTimestamp() - startTime > maxTicks) break;
        }
    }

    private void ProcessPhysicsResults()
    {
        // БЮДЖЕТ ВРЕМЕНИ: 1.0 мс.
        // Вставка статики в BepuPhysics - это перестроение дерева (BVH). 
        // Это дорогая операция. Нельзя вставлять 20 тел за кадр, иначе будет микрофриз.
        long startTime = Stopwatch.GetTimestamp();
        long maxTicks = (long)(0.001 * Stopwatch.Frequency); 

        while (_physicsResultQueue.TryDequeue(out var result))
        {
            // Проверка валидности структуры
            if (!result.IsValid) continue;

            // Используем using для гарантированного возврата массива коллайдеров
            using (result.Data) 
            {
                // Если чанк выгрузили, пока считалась физика - пропускаем
                if (result.TargetChunk == null || !result.TargetChunk.IsLoaded) continue;

                StaticHandle handle = default;
                
                // Вставка в мир физики (Main Thread)
                if (result.Data.Count > 0)
                {
                    handle = PhysicsWorld.AddStaticChunkBody(
                        (result.TargetChunk.Position * Chunk.ChunkSize).ToSystemNumerics(),
                        result.Data.CollidersArray,
                        result.Data.Count
                    );
                }
                
                // Привязываем хендл к чанку
                result.TargetChunk.OnPhysicsRebuilt(handle);
            }

            // Если время вышло - прерываемся, остальное доделаем в следующем кадре
            if (Stopwatch.GetTimestamp() - startTime > maxTicks) break;
        }
    }

    public void ProcessVoxelObjects()
    {
        while (_voxelObjectsToAdd.TryDequeue(out var vo)) _voxelObjects.Add(vo);
        foreach (var vo in _voxelObjects)
        {
            if (PhysicsWorld.Simulation.Bodies.BodyExists(vo.BodyHandle))
            {
                var pose = PhysicsWorld.GetPose(vo.BodyHandle);
                vo.UpdatePose(pose);
            }
        }
    }

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
            catch {}
        }
        _objectsToRemove.Clear();
    }

    #endregion

    #region Public API

    // --- ВОТ ЭТОГО МЕТОДА НЕ ХВАТАЛО ---
    public void NotifyVoxelFastDestroyed(Vector3i worldPos)
    {
        OnVoxelFastDestroyed?.Invoke(worldPos);
    }

    public void NotifyChunkModified(Chunk chunk)
    {
        OnChunkModified?.Invoke(chunk);
    }

    public int GetViewRangeInVoxels() => GameSettings.RenderDistance * Chunk.ChunkSize;

    public Vector3 GetPlayerPosition() => PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle)
        .Pose.Position.ToOpenTK();

    public List<Chunk> GetChunksSnapshot()
    {
        lock (_chunksLock) return new List<Chunk>(_chunks.Values);
    }

    public void QueueDetachmentCheck(Chunk chunk, Vector3i localPosition)
    {
        _detachmentQueue.Add(new DetachmentCheckTask { Chunk = chunk, RemovedVoxelLocalPos = localPosition });
    }

    public void DestroyVoxelAt(CollidableReference collidable, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        if (_staticToChunkMap.TryGetValue(collidable.StaticHandle, out var chunk))
        {
            var pointInsideVoxel = worldHitLocation - worldHitNormal * 0.001f;
            var voxelWorldPos = new Vector3i((int)Math.Floor(pointInsideVoxel.X), (int)Math.Floor(pointInsideVoxel.Y),
                (int)Math.Floor(pointInsideVoxel.Z));
            var chunkWorldOrigin = chunk.Position * Chunk.ChunkSize;
            var voxelToRemove = new Vector3i(voxelWorldPos.X - chunkWorldOrigin.X, voxelWorldPos.Y - chunkWorldOrigin.Y,
                voxelWorldPos.Z - chunkWorldOrigin.Z);

            if (chunk.RemoveVoxelAndUpdate(voxelToRemove))
            {
                OnVoxelFastDestroyed?.Invoke(chunk.Position * Chunk.ChunkSize + voxelToRemove);
            }
        }
    }

    public void RegisterChunkStatic(StaticHandle handle, Chunk chunk)
    {
        lock (_chunksLock) _staticToChunkMap[handle] = chunk;
    }

    public void UnregisterChunkStatic(StaticHandle handle)
    {
        lock (_chunksLock) _staticToChunkMap.Remove(handle);
    }

    public void QueueForRemoval(VoxelObject obj) => _objectsToRemove.Add(obj);

    #endregion

    #region Cleanup

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

    public void Dispose()
    {
        _isDisposed = true;
        _genCts.Cancel(); 
        
        _generationQueue.CompleteAdding();
        _physicsBuildQueue.CompleteAdding();
        _detachmentQueue.CompleteAdding();

        foreach(var t in _generationThreads) if (t.IsAlive) t.Join(100);
        if (_physicsBuilderThread.IsAlive) _physicsBuilderThread.Join(100);
        if (_detachmentThread.IsAlive) _detachmentThread.Join(100);

        lock (_chunksLock)
        {
            foreach (var chunk in _chunks.Values) chunk.Dispose();
            _chunks.Clear();
        }

        _generationQueue.Dispose();
        _physicsBuildQueue.Dispose();
        _detachmentQueue.Dispose();
    }
    #endregion
}