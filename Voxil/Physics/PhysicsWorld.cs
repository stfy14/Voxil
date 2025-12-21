using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using BepuUtilities.Memory;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Diagnostics; // <--- Добавлено для Stopwatch

using BepuVector3 = System.Numerics.Vector3;
using BepuQuaternion = System.Numerics.Quaternion;

public class PhysicsWorld : IDisposable
{
    public Simulation Simulation { get; }
    private readonly BufferPool _bufferPool;
    private ThreadDispatcher _threadDispatcher;
    private readonly PlayerState _player_state = new PlayerState();
    private bool _isDisposed = false;

    private readonly Dictionary<TypedIndex, Buffer<CompoundChild>> _compoundBuffers = new();
    private readonly object _bufferLock = new object();
    private readonly object _simLock = new object();

    // --- ФИКСИРОВАННЫЙ ШАГ ВРЕМЕНИ ---
    private const float SimulationTimestep = 1f / 60f; // 60 раз в секунду
    private float _timeAccumulator = 0f;

    // Таймер для дебага
    private Stopwatch _stopwatch = new Stopwatch();

    public PhysicsWorld()
    {
        _bufferPool = new BufferPool();

        // Оставляем 1-2 потока свободными для системы/рендера
        int threadCount = Math.Max(1, Environment.ProcessorCount - 2); 

        _threadDispatcher = new ThreadDispatcher(threadCount);

        var narrowPhaseCallbacks = new NarrowPhaseCallbacks { PlayerState = _player_state };
        var poseIntegratorCallbacks = new PoseIntegratorCallbacks { PlayerState = _player_state };
        var solveDescription = new SolveDescription(8, 1);

        Simulation = Simulation.Create(_bufferPool, narrowPhaseCallbacks, poseIntegratorCallbacks, solveDescription);
        Console.WriteLine($"[PhysicsWorld] Initialized with {threadCount} physics threads");
    }

    public void SetThreadCount(int count)
    {
        lock (_simLock)
        {
            if (_threadDispatcher != null && _threadDispatcher.ThreadCount == count) return;

            // Безопасно меняем диспетчер
            var oldDispatcher = _threadDispatcher;
            _threadDispatcher = new ThreadDispatcher(count);
            oldDispatcher?.Dispose();
            
            Console.WriteLine($"[Physics] Threads changed to: {count}");
        }
    }
    
    // ------------------------------------------------------------------
    // УПРАВЛЕНИЕ СТАТИКОЙ
    // ------------------------------------------------------------------

    public StaticHandle AddStaticChunkBody(
        BepuVector3 chunkWorldPosition, 
        VoxelCollider[] colliders, 
        int count) 
    {
        lock (_simLock)
        {
            if (count == 0) return new StaticHandle();

            _bufferPool.Take<CompoundChild>(count, out var children);

            for (int i = 0; i < count; i++)
            {
                var c = colliders[i]; // Работаем как с обычным массивом
                var boxShape = new Box(c.HalfSize.X * 2, c.HalfSize.Y * 2, c.HalfSize.Z * 2);
                var boxIndex = Simulation.Shapes.Add(boxShape);

                children[i] = new CompoundChild
                {
                    ShapeIndex = boxIndex,
                    LocalPosition = c.Position,
                    LocalOrientation = BepuQuaternion.Identity
                };
            }

            var childrenSlice = children.Slice(0, count);
            var compoundShape = new Compound(childrenSlice);
            var compoundIndex = Simulation.Shapes.Add(compoundShape);

            lock (_bufferLock)
            {
                _compoundBuffers.Add(compoundIndex, children);
            }

            var staticDesc = new StaticDescription(chunkWorldPosition, compoundIndex);
            return Simulation.Statics.Add(staticDesc);
        }
    }

    public void RemoveStaticBody(StaticHandle handle)
    {
        lock (_simLock)
        {
            if (Simulation.Statics.StaticExists(handle))
            {
                var staticRef = Simulation.Statics.GetStaticReference(handle);
                var shapeIndex = staticRef.Shape;

                try 
                {
                    var compoundShape = Simulation.Shapes.GetShape<Compound>(shapeIndex.Index);
                    for (int i = 0; i < compoundShape.ChildCount; i++)
                    {
                        Simulation.Shapes.Remove(compoundShape.Children[i].ShapeIndex);
                    }
                }
                catch { }

                Simulation.Shapes.Remove(shapeIndex);
                ReturnCompoundBuffer(shapeIndex);
                Simulation.Statics.Remove(handle);
            }
        }
    }

    // ------------------------------------------------------------------
    // ДИНАМИКА
    // ------------------------------------------------------------------

    public BodyHandle CreateVoxelObjectBody(
        IList<Vector3i> voxelCoordinates,
        MaterialType material,
        BepuVector3 initialPosition,
        out BepuVector3 localCenterOfMass)
    {
        lock (_simLock)
        {
            localCenterOfMass = BepuVector3.Zero;
            if (_isDisposed || voxelCoordinates == null || voxelCoordinates.Count == 0) return new BodyHandle();

            using (var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxelCoordinates.Count))
            {
                var boxShape = new Box(1f, 1f, 1f);
                float voxelMass = MaterialRegistry.Get(material).MassPerVoxel;

                foreach (var coord in voxelCoordinates)
                {
                    var pos = new BepuVector3(coord.X + 0.5f, coord.Y + 0.5f, coord.Z + 0.5f);
                    compoundBuilder.Add(boxShape, new RigidPose(pos), voxelMass);
                }

                compoundBuilder.BuildDynamicCompound(out Buffer<CompoundChild> childrenBuffer, out BodyInertia inertia, out localCenterOfMass);

                if (childrenBuffer.Length == 0)
                {
                    if (childrenBuffer.Allocated) _bufferPool.Return(ref childrenBuffer);
                    return new BodyHandle();
                }

                var compound = new Compound(childrenBuffer);
                var compoundShapeIndex = Simulation.Shapes.Add(compound);
                
                lock (_bufferLock)
                {
                    _compoundBuffers.Add(compoundShapeIndex, childrenBuffer);
                }

                var bodyDescription = BodyDescription.CreateDynamic(
                    new RigidPose(initialPosition),
                    inertia,
                    new CollidableDescription(compoundShapeIndex, 0.1f),
                    new BodyActivityDescription(-1) // -1 = Always Active
                );
                
                return Simulation.Bodies.Add(bodyDescription);
            }
        }
    }

    public BodyHandle UpdateVoxelObjectBody(
        BodyHandle handle,
        IList<Vector3i> voxelCoordinates,
        MaterialType material,
        out BepuVector3 localCenterOfMass)
    {
        localCenterOfMass = BepuVector3.Zero;
        
        lock (_simLock)
        {
            if (_isDisposed) return new BodyHandle();

            if (!Simulation.Bodies.BodyExists(handle))
            {
                return CreateVoxelObjectBody(voxelCoordinates, material, BepuVector3.Zero, out localCenterOfMass);
            }

            var bodyRef = Simulation.Bodies.GetBodyReference(handle);
            var velocity = bodyRef.Velocity;
            var pose = bodyRef.Pose;

            RemoveBody(handle);

            var newHandle = CreateVoxelObjectBody(voxelCoordinates, material, pose.Position, out localCenterOfMass);

            if (Simulation.Bodies.BodyExists(newHandle))
            {
                var newBodyRef = Simulation.Bodies.GetBodyReference(newHandle);
                newBodyRef.Velocity = velocity;
                newBodyRef.Pose.Orientation = pose.Orientation;
            }

            return newHandle;
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

                    try
                    {
                        var compoundShape = Simulation.Shapes.GetShape<Compound>(shapeIndex.Index);
                        for (int i = 0; i < compoundShape.ChildCount; i++)
                        {
                            Simulation.Shapes.Remove(compoundShape.Children[i].ShapeIndex);
                        }
                    }
                    catch { }

                    Simulation.Shapes.Remove(shapeIndex);
                    ReturnCompoundBuffer(shapeIndex);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PhysicsWorld] Error removing body: {ex.Message}");
            }
        }
    }

    public void ReturnCompoundBuffer(TypedIndex shapeIndex)
    {
        lock (_bufferLock)
        {
            if (_compoundBuffers.TryGetValue(shapeIndex, out var bufferToReturn))
            {
                if (bufferToReturn.Allocated) _bufferPool.Return(ref bufferToReturn);
                _compoundBuffers.Remove(shapeIndex);
            }
        }
    }

    public RigidPose GetPose(BodyHandle handle)
    {
        if (_isDisposed) return new RigidPose(BepuVector3.Zero);
        lock (_simLock)
        {
            if (!Simulation.Bodies.BodyExists(handle)) return new RigidPose(BepuVector3.Zero);
            return Simulation.Bodies.GetBodyReference(handle).Pose;
        }
    }

    public void SetPlayerHandle(BodyHandle playerHandle) => _player_state.BodyHandle = playerHandle;
    public void SetPlayerGoalVelocity(System.Numerics.Vector2 goalVelocity) => _player_state.GoalVelocity = goalVelocity;
    public PlayerState GetPlayerState() => _player_state;

    // --- ОБНОВЛЕННЫЙ UPDATE С ЗАМЕРАМИ ---
    public void Update(float deltaTime)
    {
        if (_isDisposed) return;

        // ЗАМЕР: Начинаем, если включен
        if (PerformanceMonitor.IsEnabled) _stopwatch.Restart();

        lock (_simLock)
        {
            if (deltaTime > 0.1f) deltaTime = 0.1f;

            _timeAccumulator += deltaTime;

            while (_timeAccumulator >= SimulationTimestep)
            {
                UpdateCharacterController();
                
                Simulation.Timestep(SimulationTimestep, _threadDispatcher);
                _timeAccumulator -= SimulationTimestep;
            }
        }

        // ЗАМЕР: Останавливаем и записываем
        if (PerformanceMonitor.IsEnabled)
        {
            _stopwatch.Stop();
            PerformanceMonitor.Record(ThreadType.Physics, _stopwatch.ElapsedTicks);
        }
    }

    private void UpdateCharacterController()
    {
        if (!Simulation.Bodies.BodyExists(_player_state.BodyHandle)) return;

        var bodyReference = Simulation.Bodies.GetBodyReference(_player_state.BodyHandle);
        var settings = _player_state.Settings;
        var bodyPosition = bodyReference.Pose.Position;

        var hitHandler = new RayHitHandler { BodyToIgnore = _player_state.BodyHandle };
        float rayLength = PlayerController.Height / 2f + settings.HoverHeight + 0.2f;

        Simulation.RayCast(bodyPosition, -BepuVector3.UnitY, rayLength, _bufferPool, ref hitHandler);

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
        BepuVector3 origin,
        BepuVector3 direction,
        float maxDistance,
        out BodyHandle hitBody,
        out BepuVector3 hitLocation,
        out BepuVector3 hitNormal,
        BodyHandle bodyToIgnore = default)
    {
        hitBody = new BodyHandle();
        hitLocation = default;
        hitNormal = default;

        if (_isDisposed) return false;

        try
        {
            direction = BepuVector3.Normalize(direction);
            var hitHandler = new RayHitHandler { BodyToIgnore = bodyToIgnore };

            lock (_simLock)
            {
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

    public void OptimizeMemory() { }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        
        lock (_simLock)
        {
            Simulation?.Dispose();
        }
        _threadDispatcher?.Dispose();
        _bufferPool?.Clear();
    }
}