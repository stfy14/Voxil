// /Physics/PhysicsWorld.cs - REFACTOURED
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using BepuUtilities.Memory;
using System;
using System.Collections.Generic;
using System.Numerics;

public class PhysicsWorld : IDisposable
{
    public Simulation Simulation { get; }
    private readonly BufferPool _bufferPool;
    private readonly ThreadDispatcher _threadDispatcher;
    private readonly PlayerState _player_state = new PlayerState();
    private bool _isDisposed = false;

    // Параметр батчинга: максимальное число детей в одном Compound
    private readonly int _batchSize;

    // Блокировка для сериализации доступа к Simulation
    private readonly object _simLock = new object();

    public PhysicsWorld(int batchSize = 512)
    {
        _batchSize = Math.Max(1, batchSize);

        _buffer_pool: ; // dummy to satisfy editer
        _bufferPool = new BufferPool();

        int threadCount = Math.Max(1, Environment.ProcessorCount >4
            ? Environment.ProcessorCount -2
            : Environment.ProcessorCount -1);

        _threadDispatcher = new ThreadDispatcher(threadCount);

        var narrowPhaseCallbacks = new NarrowPhaseCallbacks { PlayerState = _player_state };
        var poseIntegratorCallbacks = new PoseIntegratorCallbacks
        {
            PlayerState = _player_state,
            World = this
        };

        poseIntegratorCallbacks.Initialize(null);
        var solveDescription = new SolveDescription(12,2);

        Simulation = Simulation.Create(_bufferPool, narrowPhaseCallbacks, poseIntegratorCallbacks, solveDescription);

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
            Simulation.Timestep(deltaTime);
        }
    }

    /// <summary>
    /// Создаёт статические тела для чанка из вокселей.
    /// Возвращает список созданных StaticHandle (пустой список при ошибке).
    /// Разбивает на пачки, если слишком много детей.
    /// </summary>
    public List<StaticHandle> CreateStaticVoxelBody(Vector3 position, IList<OpenTK.Mathematics.Vector3i> voxelCoordinates)
    {
        var resultHandles = new List<StaticHandle>();

        if (_isDisposed || voxelCoordinates == null || voxelCoordinates.Count ==0)
        {
            Console.WriteLine("[PhysicsWorld] Cannot create static body: invalid input or disposed.");
            return resultHandles;
        }

        // Копируем координаты для безопасной итерации и деления на пачки
        var coordsCopy = new List<OpenTK.Mathematics.Vector3i>(voxelCoordinates);

        for (int start =0; start < coordsCopy.Count; start += _batchSize)
        {
            int count = Math.Min(_batchSize, coordsCopy.Count - start);
            var batch = coordsCopy.GetRange(start, count);

            var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, batch.Count);
            try
            {
                var boxShape = new Box(1,1,1);
                foreach (var coord in batch)
                {
                    var pose = new RigidPose(new Vector3(coord.X, coord.Y, coord.Z));
                    compoundBuilder.Add(boxShape, pose,1);
                }

                // Строим children для текущей пачки
                compoundBuilder.BuildKinematicCompound(out var children);

                if (children.Length ==0)
                {
                    Console.WriteLine("[PhysicsWorld] BuildKinematicCompound returned0 children for a batch!");
                    _bufferPool.Return(ref children);
                    continue;
                }

                // Создаём Compound (Compound берёт ownership children)
                var compound = new Compound(children);

                // Добавляем форму и статику под lock
                lock (_simLock)
                {
                    var shapeIndex = Simulation.Shapes.Add(compound);

                    if (!shapeIndex.Exists)
                    {
                        Console.WriteLine("[PhysicsWorld] Failed to add shape to simulation.");
                        continue;
                    }

                    var staticDescription = new StaticDescription(position, shapeIndex);

                    try
                    {
                        var staticHandle = Simulation.Statics.Add(staticDescription);
                        resultHandles.Add(staticHandle);
                    }
                    catch (Exception exInner)
                    {
                        Console.WriteLine($"[PhysicsWorld] Exception in Statics.Add: {exInner.Message}");
                        try { Console.WriteLine($"[PhysicsWorld] Statics.Count={Simulation?.Statics?.Count}"); } catch { }

                        // Попытка удалить ранее добавленную форму
                        try { if (shapeIndex.Exists) Simulation.Shapes.Remove(shapeIndex); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PhysicsWorld] Error creating static body batch: {ex.Message}");
            }
            finally
            {
                compoundBuilder.Dispose();
            }
        }

        return resultHandles;
    }

    /// <summary>
    /// Создаёт динамическое тело для вокселя-объекта
    /// </summary>
    public BodyHandle CreateVoxelObjectBody(
        IList<OpenTK.Mathematics.Vector3i> voxelCoordinates,
        MaterialType materialType,
        Vector3 initialPosition,
        out Vector3 localCenterOfMass)
    {
        localCenterOfMass = Vector3.Zero;

        if (_isDisposed || voxelCoordinates == null || voxelCoordinates.Count == 0)
        {
            Console.WriteLine("[PhysicsWorld] Cannot create dynamic body: invalid input or disposed.");
            return new BodyHandle();
        }

        var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxelCoordinates.Count);

        try
        {
            float voxelMass = MaterialRegistry.Get(materialType).MassPerVoxel;
            var boxShape = new Box(1,1,1);

            foreach (var coord in voxelCoordinates)
            {
                var pose = new RigidPose(new Vector3(coord.X, coord.Y, coord.Z));
                compoundBuilder.Add(boxShape, pose, voxelMass);
            }

            compoundBuilder.BuildDynamicCompound(out var children, out var inertia, out localCenterOfMass);

            if (children.Length ==0)
            {
                Console.WriteLine("[PhysicsWorld] BuildDynamicCompound returned0 children!");
                _bufferPool.Return(ref children);
                return new BodyHandle();
            }

            var compound = new Compound(children);

            lock (_simLock)
            {
                var shapeIndex = Simulation.Shapes.Add(compound);

                if (!shapeIndex.Exists)
                {
                    Console.WriteLine("[PhysicsWorld] Failed to add dynamic shape.");
                    _bufferPool.Return(ref children);
                    return new BodyHandle();
                }

                var bodyDescription = BodyDescription.CreateDynamic(
                    new RigidPose(initialPosition),
                    inertia,
                    new CollidableDescription(shapeIndex,0.1f),
                    new BodyActivityDescription(0.01f)
                );

                return Simulation.Bodies.Add(bodyDescription);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PhysicsWorld] Error creating dynamic body: {ex.Message}");
            return new BodyHandle();
        }
        finally
        {
            compoundBuilder.Dispose();
        }
    }

    /// <summary>
    /// Обновляет существующее динамическое тело (при изменении вокселей)
    /// </summary>
    public BodyHandle UpdateVoxelObjectBody(
        BodyHandle handle,
        IList<OpenTK.Mathematics.Vector3i> voxelCoordinates,
        MaterialType materialType,
        out Vector3 localCenterOfMass)
    {
        localCenterOfMass = Vector3.Zero;

        if (_isDisposed || voxelCoordinates == null || voxelCoordinates.Count == 0)
        {
            RemoveBody(handle);
            return new BodyHandle();
        }

        lock (_simLock)
        {
            if (!Simulation.Bodies.BodyExists(handle))
            {
                Console.WriteLine("[PhysicsWorld] Cannot update non-existent body.");
                return new BodyHandle();
            }

            var bodyReference = Simulation.Bodies.GetBodyReference(handle);
            var oldShapeIndex = bodyReference.Collidable.Shape;
            var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxelCoordinates.Count);

            try
            {
                float voxelMass = MaterialRegistry.Get(materialType).MassPerVoxel;
                var boxShape = new Box(1,1,1);

                foreach (var coord in voxelCoordinates)
                {
                    var pose = new RigidPose(new Vector3(coord.X, coord.Y, coord.Z));
                    compoundBuilder.Add(boxShape, pose, voxelMass);
                }

                compoundBuilder.BuildDynamicCompound(out var children, out var inertia, out localCenterOfMass);

                var compound = new Compound(children);
                var newShapeIndex = Simulation.Shapes.Add(compound);

                if (!newShapeIndex.Exists)
                {
                    Console.WriteLine("[PhysicsWorld] Failed to add updated shape.");
                    _bufferPool.Return(ref children);
                    return handle;
                }

                // Обновляем форму и инерцию
                bodyReference.SetShape(newShapeIndex);
                bodyReference.LocalInertia = inertia;

                // Удаляем старую форму
                Simulation.Shapes.Remove(oldShapeIndex);

                return handle;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PhysicsWorld] Error updating body: {ex.Message}");
                return handle;
            }
            finally
            {
                compoundBuilder.Dispose();
            }
        }
    }

    public void RemoveBody(BodyHandle handle)
    {
        if (_isDisposed) return;

        try
        {
            lock (_simLock)
            {
                if (Simulation.Bodies.BodyExists(handle))
                {
                    Simulation.Bodies.Remove(handle);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PhysicsWorld] Error removing body: {ex.Message}");
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
                Simulation?.Dispose();
            }
            _threadDispatcher?.Dispose();
            _bufferPool?.Clear();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PhysicsWorld] Error during disposal: {ex.Message}");
        }

        Console.WriteLine("[PhysicsWorld] Disposed.");
    }
}