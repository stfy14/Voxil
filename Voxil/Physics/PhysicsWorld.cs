// /Physics/PhysicsWorld.cs - ИСПРАВЛЕН ДЛЯ BEPU v2.5
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
    // VoxelShapeObject был удален, т.к. мы будем создавать его локально.

    private readonly int _batchSize;
    private readonly object _simLock = new object();

    public PhysicsWorld()
    {
        _bufferPool = new BufferPool();

        int threadCount = Math.Max(1, Environment.ProcessorCount > 4
            ? Environment.ProcessorCount - 2
            : Environment.ProcessorCount - 1);

        _threadDispatcher = new ThreadDispatcher(threadCount);

        var narrowPhaseCallbacks = new NarrowPhaseCallbacks { PlayerState = _player_state };
        // ВАЖНО: Мы НЕ передаем 'World = this'. Коллбэк должен быть "чистым".
        var poseIntegratorCallbacks = new PoseIntegratorCallbacks
        {
            PlayerState = _player_state
        };

        var solveDescription = new SolveDescription(8, 1);
        Simulation = Simulation.Create(_bufferPool, narrowPhaseCallbacks, poseIntegratorCallbacks, solveDescription);
        Console.WriteLine($"[PhysicsWorld] Initialized with {threadCount} physics threads");
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
        // 1. СНАЧАЛА обновляем состояние персонажа (делаем Raycast)
        UpdateCharacterController(deltaTime);
        // 2. ПОТОМ делаем шаг симуляции с уже готовыми данными
        Simulation.Timestep(deltaTime, _threadDispatcher);
    }

    // --- НОВЫЙ МЕТОД (БЕЗОПАСЕН ДЛЯ ФОНОВЫХ ПОТОКОВ) ---
    // Этот метод только ВЫЧИСЛЯЕТ форму, не трогая симуляцию.
    public Compound? CreateStaticChunkShape(Dictionary<Vector3i, MaterialType> voxels, out Buffer<CompoundChild> childrenBuffer)
    {
        childrenBuffer = default;
        if (voxels == null || voxels.Count == 0)
            return null;

        // CompoundBuilder можно безопасно использовать в любом потоке,
        // если он не пытается добавлять/удалять формы из общего Simulation.Shapes.
        using (var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxels.Count))
        {
            var boxShape = new Box(1, 1, 1);
            foreach (var voxel in voxels)
            {
                var localPosition = new Vector3(voxel.Key.X + 0.5f, voxel.Key.Y + 0.5f, voxel.Key.Z + 0.5f);
                var pose = new RigidPose(localPosition);
                compoundBuilder.Add(boxShape, pose, 1f);
            }
            compoundBuilder.BuildKinematicCompound(out childrenBuffer);
        }

        if (childrenBuffer.Length == 0)
            return null;

        return new Compound(childrenBuffer);
    }

    // --- НОВЫЙ МЕТОД (ТОЛЬКО ДЛЯ ГЛАВНОГО ПОТОКА) ---
    // Этот метод ПРИМЕНЯЕТ готовую форму к симуляции.
    public StaticHandle AddStaticChunkBody(Vector3 chunkWorldPosition, Compound compoundShape, Buffer<CompoundChild> childrenBuffer)
    {
        lock (_simLock) // Блокируем симуляцию на время изменения
        {
            var shapeIndex = Simulation.Shapes.Add(compoundShape);
            if (!shapeIndex.Exists)
            {
                _bufferPool.Return(ref childrenBuffer);
                return default;
            }

            lock (_bufferLock)
            {
                _compoundBuffers.Add(shapeIndex, childrenBuffer);
            }

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
                var boxShape = new Box(1, 1, 1); // Создаем один экземпляр
                float voxelMass = MaterialRegistry.Get(materialType).MassPerVoxel;
                foreach (var coord in voxelCoordinates)
                {
                    var pose = new RigidPose(new Vector3(coord.X + 0.5f, coord.Y + 0.5f, coord.Z + 0.5f));
                    compoundBuilder.Add(boxShape, pose, voxelMass);
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
                var boxShape = new Box(1, 1, 1); // Создаем один экземпляр
                float voxelMass = MaterialRegistry.Get(materialType).MassPerVoxel;
                foreach (var coord in voxelCoordinates)
                {
                    var pose = new RigidPose(new Vector3(coord.X + 0.5f, coord.Y + 0.5f, coord.Z + 0.5f));
                    compoundBuilder.Add(boxShape, pose, voxelMass);
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
        if (!Simulation.Bodies.BodyExists(_player_state.BodyHandle))
            return;

        var bodyReference = Simulation.Bodies.GetBodyReference(_player_state.BodyHandle);
        var settings = _player_state.Settings;
        var bodyPosition = bodyReference.Pose.Position;

        var hitHandler = new RayHitHandler { BodyToIgnore = _player_state.BodyHandle };
        float rayLength = PlayerController.Height / 2f + settings.HoverHeight + 0.2f;

        // Используем _bufferPool, как того требует Bepu v2.5
        Simulation.RayCast(bodyPosition, -Vector3.UnitY, rayLength, _bufferPool, ref hitHandler);

        // Обновляем общее состояние, которое потом прочитает PoseIntegratorCallbacks
        if (hitHandler.Hit)
        {
            _player_state.IsOnGround = true;
            _player_state.RayT = hitHandler.T;
        }
        else
        {
            _player_state.IsOnGround = false;
            _player_state.RayT = float.MaxValue;
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
                // ИСПРАВЛЕНИЕ v2.5: Добавлен аргумент _bufferPool
                Simulation.RayCast(origin, direction, maxDistance, _bufferPool, ref hitHandler);
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