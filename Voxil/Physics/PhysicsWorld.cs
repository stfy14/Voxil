// /Physics/PhysicsWorld.cs - КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ CRASH
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using BepuUtilities.Memory;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Numerics;

using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

public class PhysicsWorld : IDisposable
{
    public Simulation Simulation { get; }
    private readonly BufferPool _bufferPool;
    private readonly ThreadDispatcher _threadDispatcher;
    private readonly PlayerState _player_state = new PlayerState();
    private bool _isDisposed = false;
    private readonly Dictionary<TypedIndex, Buffer<CompoundChild>> _compoundBuffers = new();
    private readonly object _bufferLock = new object();
    public readonly Box VoxelShapeObject;

    private readonly int _batchSize;
    private readonly object _simLock = new object();

    public PhysicsWorld(int batchSize = 512)
    {
        _batchSize = Math.Max(1, batchSize);
        _bufferPool = new BufferPool();

        int threadCount = Math.Max(1, Environment.ProcessorCount > 4
            ? Environment.ProcessorCount - 2
            : Environment.ProcessorCount - 1);

        _threadDispatcher = new ThreadDispatcher(threadCount);

        var narrowPhaseCallbacks = new NarrowPhaseCallbacks { PlayerState = _player_state };
        var poseIntegratorCallbacks = new PoseIntegratorCallbacks
        {
            PlayerState = _player_state
        };

        var solveDescription = new SolveDescription(8, 1);

        Simulation = Simulation.Create(_bufferPool, narrowPhaseCallbacks, poseIntegratorCallbacks, solveDescription);

        // --- ДОБАВЬТЕ ЭТОТ КОД ПОСЛЕ Simulation.Create ---
        // 1. Создаем один-единственный "мастер-куб" для всех вокселей.
        VoxelShapeObject = new Box(1, 1, 1);
        // 2. Добавляем его в симуляцию ОДИН РАЗ, чтобы "зарегистрировать" его тип.
        //    Это гарантирует, что BepuPhysics будет знать о типе Box с самого начала.
        Simulation.Shapes.Add(VoxelShapeObject);

        Console.WriteLine($"[PhysicsWorld] Initialized with {threadCount} physics threads. BatchSize={_batchSize}");
    }

    public void SetPlayerHandle(BodyHandle playerHandle)
    {
        _player_state.BodyHandle = playerHandle;
        Console.WriteLine($"[PhysicsWorld] PlayerHandle set: {playerHandle.Value}");
    }

    public void SetPlayerGoalVelocity(Vector2 goalVelocity)
    {
        _player_state.GoalVelocity = goalVelocity;
    }

    public PlayerState GetPlayerState() => _player_state;

    public void Update(float deltaTime)
    {
        if (_isDisposed) return;
        lock (_simLock)
        {
            // СНАЧАЛА обновляем контроллер, когда симуляция стабильна
            UpdateCharacterController(deltaTime);

            // ЗАТЕМ запускаем основной шаг физики
            Simulation.Timestep(deltaTime, _threadDispatcher);
        }
    }

    /// <summary>
    /// КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: Правильное создание Compound с shape'ами
    /// </summary>
    public StaticHandle CreateStaticChunkBody(Vector3 chunkWorldPosition, Dictionary<Vector3i, MaterialType> voxels)
    {
        if (voxels == null || voxels.Count == 0) return default;

        // Паттерн остается тем же: локальный экземпляр, который мы передаем в builder.
        var boxShape = new Box(1, 1, 1);

        using (var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxels.Count))
        {
            foreach (var voxel in voxels)
            {
                var localPosition = new Vector3(voxel.Key.X + 0.5f, voxel.Key.Y + 0.5f, voxel.Key.Z + 0.5f);
                var pose = new RigidPose(localPosition);
                compoundBuilder.Add(boxShape, pose, 1f);
            }

            // --- КЛЮЧЕВОЕ ИЗМЕНЕНИЕ ---
            // Вместо BuildKinematicCompound, мы вызываем Build, чтобы получить BigCompound.
            // Этот метод сам управляет памятью и создает оптимизированное BVH-дерево.
            compoundBuilder.Build(_bufferPool, out BigCompound bigCompound);

            if (bigCompound.ChildCount == 0)
            {
                // Если compound пуст, builder мог все равно выделить буфер.
                // Мы должны его уничтожить, чтобы избежать утечки.
                bigCompound.Dispose(_bufferPool);
                return default;
            }

            // Теперь мы добавляем в симуляцию не Compound, а BigCompound.
            var shapeIndex = Simulation.Shapes.Add(bigCompound);
            if (!shapeIndex.Exists)
            {
                // Если форма не добавилась, мы все равно должны уничтожить буфер.
                bigCompound.Dispose(_bufferPool);
                return default;
            }

            // Для BigCompound не нужно вычислять смещение центра, его позиция - это позиция чанка.
            var staticDesc = new StaticDescription(chunkWorldPosition, shapeIndex);
            return Simulation.Statics.Add(staticDesc);
        }
    }


    public void ReturnCompoundBuffer(TypedIndex shapeIndex)
    {
        lock (_bufferLock)
        {
            if (_compoundBuffers.TryGetValue(shapeIndex, out var bufferToReturn))
            {
                _bufferPool.Return(ref bufferToReturn);
                _compoundBuffers.Remove(shapeIndex);
            }
        }
    }

    /// <summary>
    /// КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: Правильное создание динамических тел
    /// </summary>
    public BodyHandle CreateVoxelObjectBody(
    IList<OpenTK.Mathematics.Vector3i> voxelCoordinates,
    MaterialType materialType,
    Vector3 initialPosition,
    out Vector3 localCenterOfMass)
    {
        lock (_simLock)
        {
            localCenterOfMass = Vector3.Zero;
            if (_isDisposed || voxelCoordinates == null || voxelCoordinates.Count == 0) return new BodyHandle();

            using (var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxelCoordinates.Count))
            {
                float voxelMass = MaterialRegistry.Get(materialType).MassPerVoxel;
                foreach (var coord in voxelCoordinates)
                {
                    var pose = new RigidPose(new Vector3(coord.X, coord.Y, coord.Z));
                    // ИСПОЛЬЗУЕМ ОБЪЕКТ "мастер-куба"
                    compoundBuilder.Add(VoxelShapeObject, pose, voxelMass);
                }

                compoundBuilder.BuildDynamicCompound(out Buffer<CompoundChild> childrenBuffer, out BodyInertia inertia, out localCenterOfMass);

                if (childrenBuffer.Length == 0)
                {
                    if (childrenBuffer.Allocated) _bufferPool.Return(ref childrenBuffer);
                    return new BodyHandle();
                }

                var compound = new Compound(childrenBuffer);
                var compoundShapeIndex = Simulation.Shapes.Add(compound);
                if (!compoundShapeIndex.Exists)
                {
                    _bufferPool.Return(ref childrenBuffer);
                    return new BodyHandle();
                }

                lock (_bufferLock)
                {
                    _compoundBuffers.Add(compoundShapeIndex, childrenBuffer);
                }

                var bodyDescription = BodyDescription.CreateDynamic(
                    new RigidPose(initialPosition),
                    inertia,
                    new CollidableDescription(compoundShapeIndex, 0.1f),
                    new BodyActivityDescription(0.01f)
                );
                return Simulation.Bodies.Add(bodyDescription);
            }
        }
    }


    /// <summary>
    /// КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: Правильное обновление тел
    /// </summary>
    public BodyHandle UpdateVoxelObjectBody(
    BodyHandle handle,
    IList<OpenTK.Mathematics.Vector3i> voxelCoordinates,
    MaterialType materialType,
    out Vector3 localCenterOfMass)
    {
        lock (_simLock)
        {
            localCenterOfMass = Vector3.Zero;
            if (_isDisposed || voxelCoordinates == null || voxelCoordinates.Count == 0)
            {
                RemoveBody(handle);
                return new BodyHandle();
            }

            if (!Simulation.Bodies.BodyExists(handle))
            {
                return new BodyHandle();
            }

            var bodyReference = Simulation.Bodies.GetBodyReference(handle);
            var oldShapeIndex = bodyReference.Collidable.Shape;

            using (var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxelCoordinates.Count))
            {
                float voxelMass = MaterialRegistry.Get(materialType).MassPerVoxel;
                foreach (var coord in voxelCoordinates)
                {
                    var pose = new RigidPose(new Vector3(coord.X, coord.Y, coord.Z));
                    // ИСПОЛЬЗУЕМ ОБЪЕКТ "мастер-куба"
                    compoundBuilder.Add(VoxelShapeObject, pose, voxelMass);
                }

                compoundBuilder.BuildDynamicCompound(out Buffer<CompoundChild> childrenBuffer, out BodyInertia inertia, out localCenterOfMass);

                if (childrenBuffer.Length == 0)
                {
                    if (childrenBuffer.Allocated) _bufferPool.Return(ref childrenBuffer);
                    return handle;
                }

                var compound = new Compound(childrenBuffer);
                var newCompoundShapeIndex = Simulation.Shapes.Add(compound);

                if (!newCompoundShapeIndex.Exists)
                {
                    _bufferPool.Return(ref childrenBuffer);
                    return handle;
                }

                lock (_bufferLock)
                {
                    _compoundBuffers.Add(newCompoundShapeIndex, childrenBuffer);
                }

                bodyReference.SetShape(newCompoundShapeIndex);
                bodyReference.LocalInertia = inertia;

                if (oldShapeIndex.Exists)
                {
                    Simulation.Shapes.Remove(oldShapeIndex);
                    ReturnCompoundBuffer(oldShapeIndex);
                }

                return handle;
            }
        }
    }

    public void RemoveBody(BodyHandle handle)
    {
        if (_isDisposed) return;
        // Блокируем симуляцию на время ее изменения.
        lock (_simLock)
        {
            try
            {
                if (Simulation.Bodies.BodyExists(handle))
                {
                    var bodyReference = Simulation.Bodies.GetBodyReference(handle);
                    var shapeIndex = bodyReference.Collidable.Shape;
                    Simulation.Bodies.Remove(handle);
                    if (shapeIndex.Exists)
                    {
                        Simulation.Shapes.Remove(shapeIndex);
                        ReturnCompoundBuffer(shapeIndex);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PhysicsWorld] Error removing body: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    public RigidPose GetPose(BodyHandle handle)
    {
        if (_isDisposed) return new RigidPose(Vector3.Zero);

        lock (_simLock)
        {
            if (!Simulation.Bodies.BodyExists(handle))
                return new RigidPose(Vector3.Zero);

            return Simulation.Bodies.GetBodyReference(handle).Pose;
        }
    }

    private void UpdateCharacterController(float deltaTime)
    {
        // Проверяем, что тело игрока существует в симуляции. 
        // Это единственная необходимая проверка.
        if (!Simulation.Bodies.BodyExists(_player_state.BodyHandle)) // << ИСПРАВЛЕНО
            return;

        var bodyReference = Simulation.Bodies.GetBodyReference(_player_state.BodyHandle);
        var settings = _player_state.Settings;
        var bodyPosition = bodyReference.Pose.Position;

        // 1. ВЫПОЛНЯЕМ ОДИН ТОЧНЫЙ РЕЙКАСТ
        var hitHandler = new RayHitHandler { BodyToIgnore = _player_state.BodyHandle };
        float rayLength = PlayerController.Height / 2f + settings.HoverHeight + 0.2f;

        Simulation.RayCast(bodyPosition, -Vector3.UnitY, rayLength, ref hitHandler);

        // 2. ОБНОВЛЯЕМ СОСТОЯНИЕ И ПРИМЕНЯЕМ СИЛУ
        if (hitHandler.Hit)
        {
            _player_state.IsOnGround = true;

            float error = hitHandler.T - (PlayerController.Height / 2f + settings.HoverHeight);
            float springForce = -error * settings.SpringFrequency - bodyReference.Velocity.Linear.Y * settings.SpringDamping;

            bodyReference.ApplyLinearImpulse(new Vector3(0, springForce * deltaTime, 0));
        }
        else
        {
            _player_state.IsOnGround = false;
        }
    }

    public bool Raycast(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        out BodyHandle hitBody,
        out Vector3 hitLocation,
        out Vector3 hitNormal,
        BodyHandle bodyToIgnore = default)
    {
        hitBody = new BodyHandle();
        hitLocation = default;
        hitNormal = default;

        if (_isDisposed) return false;

        try
        {
            direction = Vector3.Normalize(direction);
            var hitHandler = new RayHitHandler { BodyToIgnore = bodyToIgnore };

            lock (_simLock)
            {
                Simulation.RayCast(origin, direction, maxDistance, ref hitHandler);
            }

            if (hitHandler.Hit)
            {
                hitBody = hitHandler.Body;
                hitLocation = origin + direction * hitHandler.T;
                hitNormal = hitHandler.Normal;
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PhysicsWorld] Raycast error: {ex.Message}");
        }

        return false;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Console.WriteLine("[PhysicsWorld] Disposing...");

        try
        {
            lock (_simLock)
            {
                // Правильный порядок освобождения
                if (Simulation?.Bodies != null)
                    Simulation.Bodies.Clear();

                if (Simulation?.Statics != null)
                    Simulation.Statics.Clear();

                if (Simulation?.Shapes != null)
                    Simulation.Shapes.Clear();

                Simulation?.Dispose();
            }

            _threadDispatcher?.Dispose();
            _bufferPool?.Clear();

            Console.WriteLine("[PhysicsWorld] Disposed successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PhysicsWorld] Error during disposal: {ex.Message}\n{ex.StackTrace}");
        }
    }
}