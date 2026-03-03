// --- START OF FILE WorldManager.cs ---

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
    public PhysicsWorld PhysicsWorld { get; }
    private readonly PlayerController _playerController;
    private readonly AsyncChunkGenerator _chunkGenerator;
    private readonly AsyncChunkPhysics _physicsBuilder;
    private readonly StructuralIntegritySystem _integritySystem;

    // Хранилище
    private readonly Dictionary<Vector3i, Chunk> _chunks = new();
    private readonly Dictionary<BodyHandle, VoxelObject> _bodyToVoxelObjectMap = new();
    private readonly Dictionary<StaticHandle, Chunk> _staticToChunkMap = new();
    private readonly List<VoxelObject> _voxelObjects = new();
    private readonly ConcurrentQueue<VoxelObjectCreationData> _objectsCreationQueue = new();
    private readonly List<VoxelObject> _objectsToRemove = new();

    // --- НОВОЕ: Для оптимизации взрывов ---
    private readonly HashSet<Chunk> _dirtyChunks = new HashSet<Chunk>(); 
    
    // Планировщик
    private Thread _schedulerThread;
    private CancellationTokenSource _schedulerCts = new CancellationTokenSource();
    private readonly ConcurrentQueue<Vector3i> _unloadQueue = new();
    
    private readonly object _chunksLock = new();
    private readonly HashSet<Vector3i> _chunksInProgress = new();

    private Vector3i _currentPlayerChunkPos = new(int.MaxValue);
    private readonly object _playerPosLock = new object();
    private volatile bool _positionChangedDirty = false;
    
    private bool _isDisposed = false;

    public event Action<Chunk> OnChunkLoaded;
    public event Action<Chunk> OnChunkModified; // Используется для обновления меша
    public event Action<Vector3i> OnChunkUnloaded;
    public event Action<Vector3i> OnVoxelFastDestroyed;
    public event Action<Chunk, Vector3i, MaterialType> OnVoxelEdited;
    private readonly ConcurrentDictionary<Vector3i, float> _staticVoxelHealth = new();
    
    public const int WorldHeightChunks = 16;
    private float _memoryLogTimer = 0f;

    // --- НАСТРОЙКИ ДИНАМИЧЕСКОГО БЮДЖЕТА ---
    private readonly Stopwatch _mainThreadStopwatch = new Stopwatch();
    private const float TargetFrameTimeMs = 1000f / 75f; 
    private const float WorldUpdateBudgetPercentage = 0.3f;

    private struct VoxelObjectCreationData 
    { 
        public List<Vector3i> Voxels; 
        public MaterialType BaseMaterial; // Переименовали для ясности (fallback)
        public Dictionary<Vector3i, uint> PerVoxelMaterials; // <--- НОВОЕ ПОЛЕ
        public System.Numerics.Vector3 WorldPosition; 
    }
    
    // СЧЕТЧИКИ
    public int LoadedChunkCount => _chunks.Count;
    public int ChunksInProgressCount => _chunksInProgress.Count;
    public int UnloadQueueCount => _unloadQueue.Count;
    public int GeneratorPendingCount => _chunkGenerator.PendingCount;
    public int GeneratorResultsCount => _chunkGenerator.ResultsCount;
    public int PhysicsUrgentCount => _physicsBuilder.UrgentCount;
    public int PhysicsPendingCount => _physicsBuilder.PendingCount;
    public int PhysicsResultsCount => _physicsBuilder.ResultsCount;

    public Dictionary<Vector3i, Chunk> GetAllChunks() => _chunks;
    public List<VoxelObject> GetAllVoxelObjects() => _voxelObjects;

    public WorldManager(PhysicsWorld physicsWorld, PlayerController playerController)
    {
        PhysicsWorld = physicsWorld;
        _playerController = playerController;
        _chunkGenerator = new AsyncChunkGenerator(12345, GameSettings.GenerationThreads);
        _physicsBuilder = new AsyncChunkPhysics();
        _integritySystem = new StructuralIntegritySystem(this);

        if (physicsWorld != null) // Защита для тестов
        {
            _schedulerThread = new Thread(SchedulerLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _schedulerThread.Start();
        }
    }
    
    public void SetGenerationThreadCount(int count) => _chunkGenerator.SetThreadCount(count);

    public void ReloadWorld()
    {
        ClearAndStopWorld();
        Console.WriteLine("[World] Restarting scheduler...");
        lock (_playerPosLock)
        {
            _currentPlayerChunkPos = new(int.MaxValue);
            _positionChangedDirty = true;
        }
        _schedulerCts = new CancellationTokenSource();
        _schedulerThread = new Thread(SchedulerLoop)
        {
            IsBackground = true,
            Name = "WorldScheduler",
            Priority = ThreadPriority.AboveNormal
        };
        _schedulerThread.Start();
        Console.WriteLine("[World] Scheduler restarted.");
    }
    
    public void ClearAndStopWorld()
    {
        Console.WriteLine("[World] Stopping and clearing world state...");
        _schedulerCts.Cancel();
        if (_schedulerThread != null && _schedulerThread.IsAlive)
        {
            _schedulerThread.Join(1000);
        }
        _chunkGenerator.ClearQueue();
        _physicsBuilder.Clear();
        while (_chunkGenerator.TryGetResult(out var result)) 
        {
            if(result.Voxels != null) 
                System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels);
        }
        while (_physicsBuilder.TryGetResult(out _)) { }
        while (_unloadQueue.TryDequeue(out _)) { }
        lock (_chunksLock)
        {
            if (_chunks.Count > 0)
            {
                Console.WriteLine($"[World] Unloading {_chunks.Count} chunks...");
                var keysToUnload = new List<Vector3i>(_chunks.Keys);
                foreach (var pos in keysToUnload)
                {
                    UnloadChunk(pos);
                }
            }
        }
        lock (_chunksLock)
        {
            _chunks.Clear();
            _chunksInProgress.Clear();
            _staticToChunkMap.Clear();
            _bodyToVoxelObjectMap.Clear();
        }
        Console.WriteLine("[World] World cleared.");
    }

    public void Update(float deltaTime)
    {
        var playerPos = GetPlayerPosition();
        var currentChunkPos = new Vector3i((int)Math.Floor(playerPos.X / Constants.ChunkSizeWorld), 0, (int)Math.Floor(playerPos.Z / Constants.ChunkSizeWorld));
        
        lock (_playerPosLock)
        {
            if (currentChunkPos != _currentPlayerChunkPos)
            {
                _currentPlayerChunkPos = currentChunkPos;
                _positionChangedDirty = true;
            }
        }

        _mainThreadStopwatch.Restart();

        double targetFrameTimeMs = 1000.0 / GameSettings.TargetFPSForBudgeting;
        double totalBudget = targetFrameTimeMs * GameSettings.WorldUpdateBudgetPercentage;

        ApplyUnloads();

        // ИЗМЕНЕНИЕ: Делим бюджет пополам (или в другой пропорции).
        // Главное — дать физике гарантированное время на исполнение.
        double physicsBudget = totalBudget * 0.6; // Физике побольше приоритета, чтобы быстрее видеть мир
        double genBudget = totalBudget * 0.4;

        // Сначала обрабатываем физику (чтобы готовые чанки сразу попали на экран)
        ProcessPhysicsResults(physicsBudget);
        
        // Потом обрабатываем новые сгенерированные данные
        // Убираем зависимость одного от другого через `if`
        ProcessGeneratedChunks(genBudget);

        ProcessNewDebris();
        ProcessVoxelObjects();
        ProcessRemovals();

        _memoryLogTimer += deltaTime;
        if (_memoryLogTimer >= 5.0f) { _memoryLogTimer = 0f; }
    }
    
    private void SchedulerLoop()
{
    Vector3i lastScheduledCenter = new(int.MaxValue);
    while (!_schedulerCts.IsCancellationRequested)
    {
        Vector3i center;
        lock(_playerPosLock) center = _currentPlayerChunkPos;
        
        if (center.X == int.MaxValue)
        {
            Thread.Sleep(100); // Можно спать подольше, если игрок еще не в мире
            continue;
        }

        bool positionChanged = false;
        if (center != lastScheduledCenter)
        {
            lastScheduledCenter = center;
            ScheduleUnloads();
            positionChanged = true;
        }

        // ИЗМЕНЕНИЕ: Убираем флаг `dirty` и используем `positionChanged`
        if (positionChanged)
        {
            lock(_playerPosLock) _positionChangedDirty = false;
        }

        int viewDist = GameSettings.RenderDistance;
        int scheduledCount = 0;

        for (int r = 0; r <= viewDist; r++)
        {
            // ИЗМЕНЕНИЕ: Главное исправление.
            // Вместо жесткой блокировки, мы просто прекращаем планирование НА ЭТОТ ТИК,
            // если видим, что система уже перегружена задачами.
            // На следующей итерации (через несколько миллисекунд) он попробует снова.
            if (_chunkGenerator.PendingCount > 150)
            {
                break; // Прервать цикл for, а не блокировать поток
            }

            // Проверка на смену позиции игрока нужна, чтобы прервать
            // долгое планирование, если игрок быстро переместился.
            if (_positionChangedDirty) break;

            bool addedInRing = false;
            ProcessRing(center, r, WorldHeightChunks, viewDist, ref addedInRing);
            if (addedInRing) scheduledCount++;
        }
        
        // Логика сна в конце для контроля частоты работы планировщика
        if (scheduledCount == 0)
        {
            // Если ничего не запланировали (мир загружен или очередь полна),
            // ждем подольше перед следующей проверкой.
            Thread.Sleep(30);
        }
        else
        {
            // Если мы добавили задачи, ждем совсем немного,
            // чтобы уступить процессорное время потокам-генераторам.
            Thread.Sleep(1);
        }
    }
}

    private void ScheduleUnloads()
    {
        Vector3i center;
        lock (_playerPosLock) center = _currentPlayerChunkPos;
        if (center.X == int.MaxValue) return;

        int viewDist = GameSettings.RenderDistance;
        long safeUnloadDistSq = (long)(viewDist + 4) * (viewDist + 4);

        lock(_chunksLock)
        {
            foreach(var pos in _chunks.Keys)
            {
                long dx = pos.X - center.X;
                long dz = pos.Z - center.Z;
                if (dx * dx + dz * dz > safeUnloadDistSq) 
                {
                    _unloadQueue.Enqueue(pos);
                }
            }
        }
    }

    private void ProcessRing(Vector3i center, int radius, int height, int maxViewDist, ref bool addedAny)
    {
        long maxDistSq = (long)maxViewDist * maxViewDist;
        if (radius == 0)
        {
            for (int y = 0; y < height; y++) 
                if (TrySchedule(new Vector3i(center.X, y, center.Z), maxDistSq, center)) addedAny = true;
            return;
        }
        for (int i = -radius; i <= radius; i++)
        {
            for (int y = 0; y < height; y++)
            {
                if (TrySchedule(new Vector3i(center.X + i, y, center.Z + radius), maxDistSq, center)) addedAny = true;
                if (TrySchedule(new Vector3i(center.X + i, y, center.Z - radius), maxDistSq, center)) addedAny = true;
                if (i > -radius && i < radius)
                {
                    if (TrySchedule(new Vector3i(center.X + radius, y, center.Z + i), maxDistSq, center)) addedAny = true;
                    if (TrySchedule(new Vector3i(center.X - radius, y, center.Z + i), maxDistSq, center)) addedAny = true;
                }
            }
        }
    }
    
    private bool TrySchedule(Vector3i pos, long maxDistSq, Vector3i center)
    {
        long dx = pos.X - center.X;
        long dz = pos.Z - center.Z;
        long distSq = dx * dx + dz * dz;
        if (distSq > maxDistSq) return false;

        lock (_chunksLock)
        {
            if (_chunks.ContainsKey(pos)) return false;
            if (_chunksInProgress.Contains(pos)) return false;
            _chunksInProgress.Add(pos);
        }

        int priority = (distSq > int.MaxValue) ? int.MaxValue : (int)distSq;
        _chunkGenerator.EnqueueTask(pos, priority);
        return true;
    }

    private void ApplyUnloads() { while (_unloadQueue.TryDequeue(out var pos)) UnloadChunk(pos); }

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
            _chunksInProgress.Remove(position);
        } 
    }
    
    private void ProcessGeneratedChunks(double budgetMs) 
    { 
        while (_chunkGenerator.TryGetResult(out var result)) 
        {
            // --- Сначала полностью обрабатываем результат ---
            lock(_chunksLock) _chunksInProgress.Remove(result.Position);
            if (result.Voxels == null) continue;

            Chunk chunkToAdd = null; 
            lock (_chunksLock) 
            {
                if (!_chunks.ContainsKey(result.Position)) 
                { 
                    chunkToAdd = new Chunk(result.Position, this); 
                    chunkToAdd.SetDataFromArray(result.Voxels); 
                    _chunks[result.Position] = chunkToAdd; 
                }
            } 
            
            System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels); 
            
            if (chunkToAdd != null) 
            { 
                // ИЗМЕНЕНИЕ: Событие OnChunkLoaded убрано отсюда.
                // Теперь мы просто отправляем чанк на построение физики.
                _physicsBuilder.EnqueueTask(chunkToAdd, urgent: true); 
            } 

            // --- И только ПОТОМ проверяем бюджет, перед тем как взять СЛЕДУЮЩИЙ ---
            if (_mainThreadStopwatch.Elapsed.TotalMilliseconds >= budgetMs) break;
        } 
    }
    
    public void SpawnComplexObject(System.Numerics.Vector3 position, List<Vector3i> localVoxels, MaterialType material, Dictionary<Vector3i, uint> perVoxelMaterials)
    {
        _objectsCreationQueue.Enqueue(new VoxelObjectCreationData 
        { 
            Voxels = localVoxels, 
            BaseMaterial = material,
            PerVoxelMaterials = perVoxelMaterials,
            WorldPosition = position 
        });
    }

    // И оставь старый метод для совместимости
    public void SpawnComplexObject(System.Numerics.Vector3 position, List<Vector3i> localVoxels, MaterialType material)
    {
        _objectsCreationQueue.Enqueue(new VoxelObjectCreationData 
        { 
            Voxels = localVoxels, 
            BaseMaterial = material, 
            PerVoxelMaterials = null, // Материалы не заданы
            WorldPosition = position 
        });
    }
    
    private void ProcessPhysicsResults(double budgetMs) 
    { 
        while (_physicsBuilder.TryGetResult(out var result)) 
        {
            // --- Сначала полностью обрабатываем результат ---
            if (!result.IsValid || result.TargetChunk == null || !result.TargetChunk.IsLoaded) continue;
            
            StaticHandle handle = default;
            if (result.Data.Count > 0 && result.Data.CollidersArray != null)
            {
                handle = PhysicsWorld.AddStaticChunkBody(
                    (result.TargetChunk.Position * Constants.ChunkSizeWorld).ToSystemNumerics(),
                    result.Data.CollidersArray,
                    result.Data.Count
                );
            }
            result.TargetChunk.OnPhysicsRebuilt(handle);

            // ИЗМЕНЕНИЕ: Вызываем OnChunkLoaded здесь, когда чанк полностью готов (с данными и физикой).
            OnChunkLoaded?.Invoke(result.TargetChunk);

            // --- И только ПОТОМ проверяем бюджет, перед тем как взять СЛЕДУЮЩИЙ ---
            if (_mainThreadStopwatch.Elapsed.TotalMilliseconds >= budgetMs) break;
        } 
    }

    private void ProcessNewDebris()
    {
        while (_objectsCreationQueue.TryDequeue(out var data))
        {
            // Используем новый конструктор (см. Шаг 4)
            var vo = new VoxelObject(data.Voxels, data.BaseMaterial, 1.0f, data.PerVoxelMaterials);
            
            vo.OnEmpty += QueueForRemoval;

            var handle = PhysicsWorld.CreateVoxelObjectBody(
                data.Voxels, 
                data.BaseMaterial, 
                1.0f, // Scale
                data.WorldPosition, 
                out var localCoM
            );

            if (handle.Value == -1) continue;

            var finalWorldPos = data.WorldPosition + localCoM;
            var bodyRef = PhysicsWorld.Simulation.Bodies.GetBodyReference(handle);
            bodyRef.Pose.Position = finalWorldPos;

            vo.InitializePhysics(handle, localCoM.ToOpenTK());

            _voxelObjects.Add(vo);
            RegisterVoxelObject(handle, vo);
        }
    }
    
    private void ProcessVoxelObjects() { foreach (var vo in _voxelObjects) { if (PhysicsWorld.Simulation.Bodies.BodyExists(vo.BodyHandle)) { var pose = PhysicsWorld.GetPose(vo.BodyHandle); vo.UpdatePose(pose); } } }
    private void ProcessRemovals() { foreach (var obj in _objectsToRemove) { try { _bodyToVoxelObjectMap.Remove(obj.BodyHandle); _voxelObjects.Remove(obj); PhysicsWorld.RemoveBody(obj.BodyHandle); obj.Dispose(); } catch { } } _objectsToRemove.Clear(); }
    public OpenTK.Mathematics.Vector3 GetPlayerPosition()
    {
        if (_playerController == null) return OpenTK.Mathematics.Vector3.Zero;
    
        // === ПРОВЕРКА НА СУЩЕСТВОВАНИЕ ТЕЛА ===
        if (PhysicsWorld.Simulation.Bodies.BodyExists(_playerController.BodyHandle))
        {
            return PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle).Pose.Position.ToOpenTK();
        }
    
        // Если тело уничтожено, возвращаем последнюю известную позицию или (0,0,0)
        // В будущем здесь будет логика респавна
        Console.WriteLine("[CRITICAL] Player body does not exist!");
        return OpenTK.Mathematics.Vector3.Zero; 
    }
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _schedulerCts.Cancel();
        if (_schedulerThread != null && _schedulerThread.IsAlive)
        {
            _schedulerThread.Join();
        }
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
    public List<Chunk> GetChunksSnapshot() { lock (_chunksLock) return new List<Chunk>(_chunks.Values); }
    public float GetViewRangeInMeters() => GameSettings.RenderDistance * Constants.ChunkSizeWorld;
    public void RebuildPhysics(Chunk chunk) { if (chunk == null || !chunk.IsLoaded) return; _physicsBuilder.EnqueueTask(chunk, urgent: true); }
    public void NotifyVoxelEdited(Chunk chunk, Vector3i pos, MaterialType mat) => OnVoxelEdited?.Invoke(chunk, pos, mat);
    public void CreateDetachedObject(List<Vector3i> globalCluster)
    {
        if (globalCluster.Count == 0) return;

        // 1. Сначала находим границы и собираем данные о материалах
        Vector3i minIdx = globalCluster[0];
        foreach (var v in globalCluster)
        {
            if (v.X < minIdx.X) minIdx.X = v.X;
            if (v.Y < minIdx.Y) minIdx.Y = v.Y;
            if (v.Z < minIdx.Z) minIdx.Z = v.Z;
        }

        List<Vector3i> localVoxels = new List<Vector3i>();
        Dictionary<Vector3i, uint> materials = new Dictionary<Vector3i, uint>();
        MaterialType dominantMat = MaterialType.Stone; // Временный дефолт

        // Читаем материалы ДО того, как удалим воксели из мира
        foreach (var v in globalCluster)
        {
            Vector3i localPos = v - minIdx;
            localVoxels.Add(localPos);
            
            MaterialType mat = GetMaterialGlobal(v);
            if (mat != MaterialType.Air)
            {
                materials[localPos] = (uint)mat;
                dominantMat = mat; // Запоминаем последний как "базовый"
            }
        }

        // 2. Теперь удаляем их из мира
        foreach (var pos in globalCluster) 
            RemoveVoxelGlobal(pos);

        // 3. Сортировка для порядка
        localVoxels.Sort((a, b) => {
            if (a.X != b.X) return a.X.CompareTo(b.X);
            if (a.Y != b.Y) return a.Y.CompareTo(b.Y);
            return a.Z.CompareTo(b.Z);
        });

        System.Numerics.Vector3 worldPos = new System.Numerics.Vector3(
            minIdx.X * Constants.VoxelSize,
            minIdx.Y * Constants.VoxelSize,
            minIdx.Z * Constants.VoxelSize
        );

        // 4. Отправляем в очередь с материалами
        _objectsCreationQueue.Enqueue(new VoxelObjectCreationData
        {
            Voxels = localVoxels,
            BaseMaterial = dominantMat, // Используем найденный материал как базу
            PerVoxelMaterials = materials,
            WorldPosition = worldPos
        });
    }
    
    public void QueueForRemoval(VoxelObject obj) => _objectsToRemove.Add(obj);
    // В методе DestroyVoxelAt, секция Dynamic Objects
    
    public void ProcessDynamicObjectSplits(VoxelObject vo)
    {
        if (vo.VoxelCoordinates.Count == 0) return;

        var clusters = vo.GetConnectedClusters();
        if (clusters.Count == 1)
        {
            // Объект остался целым, просто обновляем его сетку и коллизию
            vo.RebuildMeshAndPhysics(PhysicsWorld);
        }
        else
        {
            // Объект распался на несколько частей!
            clusters.Sort((a, b) => b.Count.CompareTo(a.Count));
            
            // Самый большой кусок остается оригинальным объектом
            var mainCluster = clusters[0];
            var mainSet = new HashSet<Vector3i>(mainCluster);
            vo.VoxelCoordinates.RemoveAll(v => !mainSet.Contains(v));
            
            var savedPos = vo.Position;
            var savedCoM = vo.LocalCenterOfMass;
            var savedRot = vo.Rotation;
            
            vo.RebuildMeshAndPhysics(PhysicsWorld);

            // Остальные куски становятся новыми физическими объектами (осколками)
            for (int i = 1; i < clusters.Count; i++)
            {
                SpawnSplitCluster(clusters[i], vo, savedPos, savedCoM, savedRot);
            }
        }
    }
    
    public void GetStaticVoxelHealthInfo(Vector3i globalPos, out float currentHP, out float maxHP)
    {
        var mat = GetMaterialGlobal(globalPos);
        maxHP = MaterialRegistry.Get(mat).Hardness;
        
        if (_staticVoxelHealth.TryGetValue(globalPos, out float hp)) currentHP = hp;
        else currentHP = maxHP;
    }
    
    public void DestroyVoxelAt(CollidableReference collidable, BepuVector3 worldHitLocation, BepuVector3 worldHitNormal)
    {
        var pointInside = worldHitLocation - worldHitNormal * (Constants.VoxelSize * 0.5f);

        if (collidable.Mobility == CollidableMobility.Static)
        {
            Vector3i globalVoxelIndex = new Vector3i(
                (int)Math.Floor(pointInside.X / Constants.VoxelSize), 
                (int)Math.Floor(pointInside.Y / Constants.VoxelSize), 
                (int)Math.Floor(pointInside.Z / Constants.VoxelSize)
            );
            
            // Ручной клик ЛКМ ломает статику моментально (без учета ХП)
            if (RemoveVoxelGlobal(globalVoxelIndex))
            {
                NotifyVoxelFastDestroyed(globalVoxelIndex);
                _integritySystem.QueueCheck(globalVoxelIndex);
            }
        }
        else if (collidable.Mobility == CollidableMobility.Dynamic && 
                 _bodyToVoxelObjectMap.TryGetValue(collidable.BodyHandle, out var voxelObj))
        {
            float alpha = PhysicsWorld.PhysicsAlpha;
            Matrix4 modelMatrix = voxelObj.GetInterpolatedModelMatrix(alpha);
            Matrix4 invModel = Matrix4.Invert(modelMatrix);

            Vector4 localHitRaw = new Vector4(worldHitLocation.ToOpenTK(), 1.0f) * invModel;
            Vector3 localHitPos = localHitRaw.Xyz;

            Quaternion invRot = Quaternion.Invert(Quaternion.Slerp(voxelObj.PrevRotation, voxelObj.Rotation, alpha));
            Vector3 localNormal = Vector3.Transform(worldHitNormal.ToOpenTK(), invRot);

            Vector3 pointInsideLocal = localHitPos - localNormal * (Constants.VoxelSize * 0.5f);

            Vector3i localVoxelIndex = new Vector3i(
                (int)Math.Floor(pointInsideLocal.X / Constants.VoxelSize),
                (int)Math.Floor(pointInsideLocal.Y / Constants.VoxelSize),
                (int)Math.Floor(pointInsideLocal.Z / Constants.VoxelSize)
            );

            // Ручной клик ЛКМ ломает динамику моментально
            if (voxelObj.RemoveVoxel(localVoxelIndex))
            {
                // Вызываем новый алгоритм раскола
                ProcessDynamicObjectSplits(voxelObj);
            }
        }
    }

    
    public MaterialType GetMaterialGlobal(Vector3i globalPos)
    {
        lock (_chunksLock) // <--- ГЛАВНОЕ ИСПРАВЛЕНИЕ!
        {
            Vector3i chunkPos = GetChunkPosFromVoxelIndex(globalPos);
            if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded)
            {
                int res = Constants.ChunkResolution;
                int lx = globalPos.X % res; if (lx < 0) lx += res;
                int ly = globalPos.Y % res; if (ly < 0) ly += res;
                int lz = globalPos.Z % res; if (lz < 0) lz += res;
                return chunk.GetMaterialAt(new Vector3i(lx, ly, lz));
            }
            return MaterialType.Air;
        }
    }

    // 1. Спавн динамического объекта (Динамит)
    public void SpawnDynamicObject(VoxelObject vo, System.Numerics.Vector3 position, System.Numerics.Vector3 velocity)
    {
        var handle = PhysicsWorld.CreateVoxelObjectBody(vo.VoxelCoordinates, vo.Material, vo.Scale, position, out var localCoM);
    
        // ФУНДАМЕНТАЛЬНО: Объект физически не смог создаться (0 коллайдеров, материал-воздух и т.д.)
        if (handle.Value == -1)
        {
            Console.WriteLine($"[Physics] Rejected invalid dynamic object: {vo.Material}");
            return; // Объект просто не спавнится, мир в безопасности
        }
        
        var bodyRef = PhysicsWorld.Simulation.Bodies.GetBodyReference(handle);
        bodyRef.Velocity.Linear = velocity;
        bodyRef.Awake = true;

        // === СТАРЫЙ НЕРАБОЧИЙ КОД УДАЛЕН ===

        vo.InitializePhysics(handle, localCoM.ToOpenTK());
    
        _voxelObjects.Add(vo);
        lock (_chunksLock) _bodyToVoxelObjectMap[handle] = vo;
    }
    
    // 2. Уничтожение объекта (Динамит взорвался)
    public void DestroyVoxelObject(VoxelObject vo)
    {
        if (vo == null) return;
        QueueForRemoval(vo);
    }
    // 3. Публичное удаление вокселя (для взрывов)
    // Добавили флаг updateMesh, чтобы не перестраивать чанк 1000 раз при взрыве
    public bool RemoveVoxelGlobal(Vector3i globalVoxelIndex, bool updateMesh = true)
    {
        Vector3i chunkPos = GetChunkPosFromVoxelIndex(globalVoxelIndex);
        if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded)
        {
            int res = Constants.ChunkResolution;
            int lx = globalVoxelIndex.X % res; if (lx < 0) lx += res;
            int ly = globalVoxelIndex.Y % res; if (ly < 0) ly += res;
            int lz = globalVoxelIndex.Z % res; if (lz < 0) lz += res;
            
            // Вызываем удаление внутри чанка
            // ВАЖНО: В Chunk.cs тоже нужно будет подправить RemoveVoxelAndUpdate, 
            // но пока используем старый метод, он вернет true если удалил
            bool removed = chunk.RemoveVoxelAndUpdate(new Vector3i(lx, ly, lz));
            
            if (removed)
            {
                // Если updateMesh = false, мы не перестраиваем физику и GPU буферы немедленно
                // А просто помечаем чанк как "грязный"
                if (!updateMesh)
                {
                    lock(_dirtyChunks) _dirtyChunks.Add(chunk);
                }
                return true;
            }
        }
        return false;
    }
    
    public void ApplyDamageToStatic(Vector3i globalPos, float damage, out bool destroyed)
    {
        destroyed = false;
        var mat = GetMaterialGlobal(globalPos);
        if (mat == MaterialType.Air) return;

        float maxHealth = MaterialRegistry.Get(mat).Hardness;

        // Потокобезопасное обновление здоровья
        float newHealth = _staticVoxelHealth.AddOrUpdate(
            globalPos,
            maxHealth - damage,
            (k, currentVal) => currentVal - damage
        );

        if (newHealth <= 0)
        {
            _staticVoxelHealth.TryRemove(globalPos, out _);
            if (RemoveVoxelGlobal(globalPos, false))
            {
                NotifyVoxelFastDestroyed(globalPos);
                _integritySystem.QueueCheck(globalPos);
                destroyed = true;
            }
        }
    }
    
    // Перегрузка для старого кода (одиночный клик ЛКМ)
    public bool RemoveVoxelGlobal(Vector3i globalVoxelIndex) => RemoveVoxelGlobal(globalVoxelIndex, true);
    
    // 4. Пометить чанк грязным (вызывается из ExplosionSystem)
    public void MarkChunkDirty(Vector3i globalVoxelIndex)
    {
        Vector3i chunkPos = GetChunkPosFromVoxelIndex(globalVoxelIndex);
        if (_chunks.TryGetValue(chunkPos, out var chunk))
        {
            lock(_dirtyChunks) _dirtyChunks.Add(chunk);
        }
    }
    // 5. Массовое обновление грязных чанков (после взрыва)
    public void UpdateDirtyChunks()
    {
        Chunk[] dirtyArr;
        lock (_dirtyChunks)
        {
            dirtyArr = _dirtyChunks.ToArray();
            _dirtyChunks.Clear();
        }

        foreach (var chunk in dirtyArr)
        {
            // Обновляем визуальную часть (GPU)
            OnChunkModified?.Invoke(chunk); 
            // Обновляем физику
            RebuildPhysics(chunk);
        }
    }

    private void SpawnSplitCluster(
    List<Vector3i> localClusterVoxels, 
    VoxelObject parentObj,
    Vector3 savedParentPos,    // Позиция ДО пересчёта
    Vector3 savedParentCoM,    // Центр масс ДО пересчёта
    Quaternion savedParentRot) // Поворот (обычно не меняется, но для надёжности)
{
    if (localClusterVoxels.Count == 0) return;

    Vector3i anchorIdx = localClusterVoxels[0];
    List<Vector3i> newLocalVoxels = new List<Vector3i>();
    Dictionary<Vector3i, uint> newMaterials = new Dictionary<Vector3i, uint>();

    foreach (var originalPosInParent in localClusterVoxels)
    {
        Vector3i newPos = originalPosInParent - anchorIdx;
        newLocalVoxels.Add(newPos);
        if (parentObj.VoxelMaterials.TryGetValue(originalPosInParent, out uint matId))
            newMaterials[newPos] = matId;
        else
            newMaterials[newPos] = (uint)parentObj.Material;
    }

    // Используем savedParentCoM и savedParentPos!
    System.Numerics.Vector3 anchorPosInParentLocal = 
        (anchorIdx.ToSystemNumerics() - savedParentCoM.ToSystemNumerics()) 
        * Constants.VoxelSize * parentObj.Scale;
    
    System.Numerics.Vector3 anchorWorldPos = savedParentPos.ToSystemNumerics() + 
        System.Numerics.Vector3.Transform(anchorPosInParentLocal, savedParentRot.ToSystemNumerics());

    var newObj = new VoxelObject(newLocalVoxels, parentObj.Material, parentObj.Scale, newMaterials);
    newObj.OnEmpty += QueueForRemoval;

    var handle = PhysicsWorld.CreateVoxelObjectBody(
        newLocalVoxels, newObj.Material, newObj.Scale, anchorWorldPos, out var newBodyCoM);

    if (handle.Value == -1) return;
    
    var bodyRef = PhysicsWorld.Simulation.Bodies.GetBodyReference(handle);
    System.Numerics.Vector3 rotatedCoMOffset = System.Numerics.Vector3.Transform(
        newBodyCoM, savedParentRot.ToSystemNumerics());
    
    // ЭТО БЫЛ ЗНАК "-", НУЖЕН "+"!
    // anchorWorldPos — это угол вокселя, CoM внутри этого вокселя смещён на +rotatedCoMOffset
    bodyRef.Pose.Position = anchorWorldPos + rotatedCoMOffset;
    bodyRef.Pose.Orientation = savedParentRot.ToSystemNumerics();
    
    if (PhysicsWorld.Simulation.Bodies.BodyExists(parentObj.BodyHandle))
        bodyRef.Velocity = PhysicsWorld.Simulation.Bodies.GetBodyReference(parentObj.BodyHandle).Velocity;

    newObj.InitializePhysics(handle, newBodyCoM.ToOpenTK());
    _voxelObjects.Add(newObj);
    RegisterVoxelObject(handle, newObj);
}
    private Vector3i GetChunkPosFromVoxelIndex(Vector3i voxelIndex) => new Vector3i((int)Math.Floor((float)voxelIndex.X / Constants.ChunkResolution), (int)Math.Floor((float)voxelIndex.Y / Constants.ChunkResolution), (int)Math.Floor((float)voxelIndex.Z / Constants.ChunkResolution));
    public bool IsVoxelSolidGlobal(Vector3i globalVoxelIndex) { Vector3i chunkPos = GetChunkPosFromVoxelIndex(globalVoxelIndex); if (_chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded) { int res = Constants.ChunkResolution; int lx = globalVoxelIndex.X % res; if (lx < 0) lx += res; int ly = globalVoxelIndex.Y % res; if (ly < 0) ly += res; int lz = globalVoxelIndex.Z % res; if (lz < 0) lz += res; return chunk.IsVoxelSolidAt(new Vector3i(lx, ly, lz)); } return false; }
    public bool IsChunkLoadedAt(Vector3i globalVoxelIndex) { Vector3i chunkPos = GetChunkPosFromVoxelIndex(globalVoxelIndex); lock(_chunksLock) return _chunks.ContainsKey(chunkPos) && _chunks[chunkPos].IsLoaded; }
    public void NotifyVoxelFastDestroyed(Vector3i worldPos) => OnVoxelFastDestroyed?.Invoke(worldPos);
    public void NotifyChunkModified(Chunk chunk) => OnChunkModified?.Invoke(chunk);
    public void RegisterVoxelObject(BodyHandle handle, VoxelObject obj) { lock (_chunksLock) _bodyToVoxelObjectMap[handle] = obj; }
    public void RegisterChunkStatic(StaticHandle handle, Chunk chunk) { lock (_chunksLock) _staticToChunkMap[handle] = chunk; }
    public void UnregisterChunkStatic(StaticHandle handle) { lock (_chunksLock) _staticToChunkMap.Remove(handle); }
    
    // Добавляем НОВЫЙ МЕТОД в конец файла WorldManager.cs
    public System.Numerics.Vector3 GetPlayerVelocity()
    {
        if (_playerController != null && PhysicsWorld.Simulation.Bodies.BodyExists(_playerController.BodyHandle))
        {
            return PhysicsWorld.Simulation.Bodies.GetBodyReference(_playerController.BodyHandle).Velocity.Linear;
        }
        return System.Numerics.Vector3.Zero;
    }
    public void SpawnTestVoxel(System.Numerics.Vector3 position, MaterialType material) { var voxels = new List<Vector3i> { new Vector3i(0, 0, 0) }; _objectsCreationQueue.Enqueue(new VoxelObjectCreationData { Voxels = voxels, BaseMaterial = material, PerVoxelMaterials = null, WorldPosition = position }); }
    public void TestBreakVoxel(VoxelObject vo, Vector3i localPos)
    {
        if (vo.RemoveVoxel(localPos))
        {
            if (vo.VoxelCoordinates.Count == 0) return;

            var clusters = vo.GetConnectedClusters();
            if (clusters.Count == 1)
            {
                vo.RebuildMeshAndPhysics(PhysicsWorld);
            }
            else
            {
                clusters.Sort((a, b) => b.Count.CompareTo(a.Count));
                var mainCluster = clusters[0];
                var mainSet = new HashSet<Vector3i>(mainCluster);
                vo.VoxelCoordinates.RemoveAll(v => !mainSet.Contains(v));
                var savedPos = vo.Position;
                var savedCoM = vo.LocalCenterOfMass;
                var savedRot = vo.Rotation;

                vo.RebuildMeshAndPhysics(PhysicsWorld);

                for (int i = 1; i < clusters.Count; i++)
                {
                    SpawnSplitCluster(clusters[i], vo, savedPos, savedCoM, savedRot);
                }
            }
        }
    }
}