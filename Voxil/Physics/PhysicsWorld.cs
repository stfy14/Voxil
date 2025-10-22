// /Physics/PhysicsWorld.cs
using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using OpenTK.Mathematics;

// Алиасы, чтобы избежать неоднозначности Vector3
using NumVec3 = System.Numerics.Vector3;
using TKVec3 = OpenTK.Mathematics.Vector3;
using TKVec3i = OpenTK.Mathematics.Vector3i;

/// <summary>
/// Централизованная, безопасная обёртка вокруг Bepu Physics Simulation.
/// - Fixed timestep (по умолчанию 60 Hz) + substepping.
/// - Интерполяция поз для рендера.
/// - Управление BufferPool / CompoundBuilder с гарантированным возвратом буферов.
/// - Refcount для форм (TypedIndex).
/// - Потокобезопасный доступ через lock.
/// </summary>
public class PhysicsWorld : IDisposable
{
    public Simulation Simulation { get; private set; }

    private readonly BufferPool _bufferPool;
    private readonly ThreadDispatcher _threadDispatcher;
    private readonly PlayerState _player_state = new PlayerState();

    private readonly object _simLock = new object();
    private bool _isDisposed = false;

    private readonly int _batchSize;

    // shape refcount
    private readonly Dictionary<TypedIndex, int> _shapeRefCount = new();

    // fixed timestep + accumulator
    private readonly float _fixedTimestep;
    private float _accumulator = 0f;
    private const float DefaultFixedTimestep = 1f / 60f;

    // interpolation tracking
    private readonly HashSet<BodyHandle> _trackedBodies = new();
    private readonly Dictionary<BodyHandle, RigidPose> _previousPoses = new();
    private readonly Dictionary<BodyHandle, RigidPose> _currentPoses = new();

    // thread count (dispatcher)
    public PhysicsWorld(int batchSize = 512, float fixedTimestep = DefaultFixedTimestep, int? threadCount = null)
    {
        _batchSize = Math.Max(1, batchSize);
        _fixedTimestep = fixedTimestep > 0 ? fixedTimestep : DefaultFixedTimestep;

        _bufferPool = new BufferPool();

        int tc = threadCount ?? Math.Max(1, Environment.ProcessorCount > 4 ? Environment.ProcessorCount - 2 : Environment.ProcessorCount - 1);
        _threadDispatcher = new ThreadDispatcher(tc);

        var narrowPhaseCallbacks = new NarrowPhaseCallbacks { PlayerState = _player_state };
        var poseIntegratorCallbacks = new PoseIntegratorCallbacks { PlayerState = _player_state, World = this };

        var solveDescription = new SolveDescription(12, 2);

        Simulation = Simulation.Create(_bufferPool, narrowPhaseCallbacks, poseIntegratorCallbacks, solveDescription);

        Console.WriteLine($"[PhysicsWorld] Initialized. Threads={tc}, BatchSize={_batchSize}, FixedStep={_fixedTimestep:F6}s");
    }

    // ---------------------------
    // Player helpers
    // ---------------------------
    public void SetPlayerHandle(BodyHandle playerHandle)
    {
        _player_state.BodyHandle = playerHandle;
        RegisterBodyForInterpolation(playerHandle);
        Console.WriteLine($"[PhysicsWorld] PlayerHandle set: {playerHandle.Value}");
    }

    public void SetPlayerGoalVelocity(System.Numerics.Vector2 goalVelocity)
    {
        _player_state.GoalVelocity = goalVelocity;
    }

    public PlayerState GetPlayerState() => _player_state;

    // ---------------------------
    // Update loop: fixed-step + interpolation bookkeeping
    // ---------------------------
    public void Update(float deltaTime)
    {
        if (_isDisposed) return;
        if (deltaTime < 0) deltaTime = 0;

        lock (_simLock)
        {
            _accumulator += deltaTime;

            // If no full step yet, we might still want to keep current poses consistent.
            while (_accumulator >= _fixedTimestep)
            {
                // snapshot current to previous for interpolation
                SnapshotCurrentPosesToPrevious();

                try
                {
                    Simulation.Timestep(_fixedTimestep, _threadDispatcher);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PhysicsWorld] Exception during Timestep: {ex}");
                }

                UpdateCurrentPosesFromSimulation();

                _accumulator -= _fixedTimestep;
            }
        }
    }

    /// <summary>
    /// Возвращает интерполяцию позы тела между предыдущим и текущим состоянием.
    /// alpha 0 -> previous, 1 -> current.
    /// </summary>
    public RigidPose GetInterpolatedPose(BodyHandle handle, float alpha)
    {
        if (_isDisposed) return new RigidPose(NumVec3.Zero);
        alpha = Math.Clamp(alpha, 0f, 1f);

        lock (_simLock)
        {
            if (!_currentPoses.TryGetValue(handle, out var cur))
            {
                try
                {
                    if (Simulation.Bodies.BodyExists(handle))
                        return Simulation.Bodies.GetBodyReference(handle).Pose;
                }
                catch { }
                return new RigidPose(NumVec3.Zero);
            }
            if (!_previousPoses.TryGetValue(handle, out var prev))
                return cur;

            var pos = NumVec3.Lerp(prev.Position, cur.Position, alpha);
            var ori = NumVec3QuaternionSlerp(prev.Orientation, cur.Orientation, alpha);
            return new RigidPose(pos, ori);
        }
    }

    // Small helper: slerp for System.Numerics.Quaternion (vectorized minimal impl)
    private static System.Numerics.Quaternion NumVec3QuaternionSlerp(System.Numerics.Quaternion a, System.Numerics.Quaternion b, float t)
    {
        // Avoid using Unity-style api - implement a simple slerp
        float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        const float DOT_THRESHOLD = 0.9995f;

        if (Math.Abs(dot) > DOT_THRESHOLD)
        {
            // linear interpolation for very close quaternions
            var result = new System.Numerics.Quaternion(
                a.X + t * (b.X - a.X),
                a.Y + t * (b.Y - a.Y),
                a.Z + t * (b.Z - a.Z),
                a.W + t * (b.W - a.W)
            );
            return System.Numerics.Quaternion.Normalize(result);
        }

        // slerp
        dot = Math.Clamp(dot, -1f, 1f);
        float theta0 = (float)Math.Acos(dot);
        float theta = theta0 * t;
        float sinTheta = (float)Math.Sin(theta);
        float sinTheta0 = (float)Math.Sin(theta0);

        float s0 = (float)Math.Cos(theta) - dot * sinTheta / sinTheta0;
        float s1 = sinTheta / sinTheta0;

        return new System.Numerics.Quaternion(
            s0 * a.X + s1 * b.X,
            s0 * a.Y + s1 * b.Y,
            s0 * a.Z + s1 * b.Z,
            s0 * a.W + s1 * b.W
        );
    }

    // ---------------------------
    // Body / shape creation & updates
    // ---------------------------

    /// <summary>
    /// Создаёт статические тела (чанковые пачки) из локальных воксельных координат.
    /// Важно: гарантированный возврат children в pool при ошибках.
    /// </summary>
    public List<StaticHandle> CreateStaticVoxelBody(NumVec3 position, IList<TKVec3i> voxelCoordinates)
    {
        var results = new List<StaticHandle>();
        if (_isDisposed || voxelCoordinates == null || voxelCoordinates.Count == 0)
        {
            Console.WriteLine("[PhysicsWorld] CreateStaticVoxelBody: invalid input or disposed.");
            return results;
        }

        var coordsCopy = new List<TKVec3i>(voxelCoordinates);
        for (int start = 0; start < coordsCopy.Count; start += _batchSize)
        {
            int count = Math.Min(_batchSize, coordsCopy.Count - start);
            var batch = coordsCopy.GetRange(start, count);

            var builder = new CompoundBuilder(_bufferPool, Simulation.Shapes, batch.Count);
            Buffer<CompoundChild> children = default;
            bool childrenTaken = false;

            try
            {
                var box = new Box(1f, 1f, 1f);
                foreach (var c in batch)
                {
                    var pose = new RigidPose(new NumVec3(c.X, c.Y, c.Z));
                    builder.Add(box, pose, 1f);
                }

                builder.BuildKinematicCompound(out children);

                if (children.Length == 0)
                {
                    // Nothing produced - return any buffers allocated
                    try { _bufferPool.Return(ref children); } catch { }
                    continue;
                }

                var compound = new Compound(children);

                lock (_simLock)
                {
                    TypedIndex shapeIndex;
                    try
                    {
                        shapeIndex = Simulation.Shapes.Add(compound);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PhysicsWorld] Shapes.Add failed: {ex}");
                        try { _bufferPool.Return(ref children); } catch { }
                        continue;
                    }

                    // We treat shapeIndex as "added" if no exception. Track ref.
                    AddShapeRef(shapeIndex);

                    var staticDesc = new StaticDescription(position, shapeIndex);
                    try
                    {
                        var sh = Simulation.Statics.Add(staticDesc);
                        results.Add(sh);
                        childrenTaken = true; // shapes/compound own children now
                    }
                    catch (Exception exStat)
                    {
                        Console.WriteLine($"[PhysicsWorld] Statics.Add exception: {exStat}");
                        // rollback: release shape ref and try remove shape
                        ReleaseShapeRef(shapeIndex);
                        try { Simulation.Shapes.Remove(shapeIndex); } catch { }
                        try { _bufferPool.Return(ref children); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PhysicsWorld] Exception in CreateStaticVoxelBody batch: {ex}");
                try { if (!childrenTaken && children.Length > 0) _bufferPool.Return(ref children); } catch { }
            }
            finally
            {
                try { builder.Dispose(); } catch { }
            }
        }

        return results;
    }

    /// <summary>
    /// Создаёт динамическое тело для отдельного VoxelObject.
    /// Возвращает BodyHandle и out local center of mass.
    /// </summary>
    public BodyHandle CreateVoxelObjectBody(IList<TKVec3i> voxelCoordinates, MaterialType materialType, NumVec3 initialPosition, out NumVec3 localCenterOfMass)
    {
        localCenterOfMass = NumVec3.Zero;
        if (_isDisposed || voxelCoordinates == null || voxelCoordinates.Count == 0)
        {
            Console.WriteLine("[PhysicsWorld] CreateVoxelObjectBody: invalid input or disposed.");
            return new BodyHandle();
        }

        var builder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxelCoordinates.Count);
        Buffer<CompoundChild> children = default;
        bool childrenTaken = false;

        try
        {
            float voxelMass = MaterialRegistry.Get(materialType).MassPerVoxel;
            var box = new Box(1f, 1f, 1f);
            foreach (var c in voxelCoordinates)
            {
                var pose = new RigidPose(new NumVec3(c.X, c.Y, c.Z));
                builder.Add(box, pose, voxelMass);
            }

            BodyInertia inertia;
            builder.BuildDynamicCompound(out children, out inertia, out localCenterOfMass);

            if (children.Length == 0)
            {
                try { _bufferPool.Return(ref children); } catch { }
                Console.WriteLine("[PhysicsWorld] BuildDynamicCompound returned 0 children.");
                return new BodyHandle();
            }

            var compound = new Compound(children);

            lock (_simLock)
            {
                TypedIndex shapeIndex;
                try
                {
                    shapeIndex = Simulation.Shapes.Add(compound);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PhysicsWorld] Shapes.Add failed (dynamic): {ex}");
                    try { _bufferPool.Return(ref children); } catch { }
                    return new BodyHandle();
                }

                AddShapeRef(shapeIndex);

                var bodyDesc = BodyDescription.CreateDynamic(
                    new RigidPose(initialPosition),
                    inertia,
                    new CollidableDescription(shapeIndex, 0.1f),
                    new BodyActivityDescription(0.01f)
                );

                var handle = Simulation.Bodies.Add(bodyDesc);
                RegisterBodyForInterpolation(handle);
                childrenTaken = true;
                return handle;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PhysicsWorld] Exception in CreateVoxelObjectBody: {ex}");
            return new BodyHandle();
        }
        finally
        {
            try { builder.Dispose(); } catch { }
            try { if (!childrenTaken && children.Length > 0) _bufferPool.Return(ref children); } catch { }
        }
    }

    /// <summary>
    /// Обновляет форму существующего динамического тела.
    /// </summary>
    public BodyHandle UpdateVoxelObjectBody(BodyHandle handle, IList<TKVec3i> voxelCoordinates, MaterialType materialType, out NumVec3 localCenterOfMass)
    {
        localCenterOfMass = NumVec3.Zero;

        if (_isDisposed) return handle;
        if (voxelCoordinates == null || voxelCoordinates.Count == 0)
        {
            RemoveBody(handle);
            return new BodyHandle();
        }

        lock (_simLock)
        {
            if (!Simulation.Bodies.BodyExists(handle))
            {
                Console.WriteLine("[PhysicsWorld] UpdateVoxelObjectBody: body does not exist.");
                return handle;
            }

            var bodyRef = Simulation.Bodies.GetBodyReference(handle);
            var oldShape = bodyRef.Collidable.Shape;

            var builder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxelCoordinates.Count);
            Buffer<CompoundChild> children = default;
            bool childrenTaken = false;

            try
            {
                float voxelMass = MaterialRegistry.Get(materialType).MassPerVoxel;
                var box = new Box(1f, 1f, 1f);
                foreach (var c in voxelCoordinates)
                {
                    var pose = new RigidPose(new NumVec3(c.X, c.Y, c.Z));
                    builder.Add(box, pose, voxelMass);
                }

                BodyInertia inertia;
                builder.BuildDynamicCompound(out children, out inertia, out localCenterOfMass);

                if (children.Length == 0)
                {
                    try { _bufferPool.Return(ref children); } catch { }
                    Console.WriteLine("[PhysicsWorld] BuildDynamicCompound returned 0 children on update.");
                    return handle;
                }

                var compound = new Compound(children);

                TypedIndex newShape;
                try
                {
                    newShape = Simulation.Shapes.Add(compound);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PhysicsWorld] Shapes.Add failed on update: {ex}");
                    try { _bufferPool.Return(ref children); } catch { }
                    return handle;
                }

                AddShapeRef(newShape);

                // update shape and inertia on body
                bodyRef.SetShape(newShape);
                bodyRef.LocalInertia = inertia;

                // release old shape
                ReleaseShapeRef(oldShape);

                childrenTaken = true;
                return handle;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PhysicsWorld] Exception in UpdateVoxelObjectBody: {ex}");
                return handle;
            }
            finally
            {
                try { builder.Dispose(); } catch { }
                try { if (!childrenTaken && children.Length > 0) _bufferPool.Return(ref children); } catch { }
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
                    Simulation.Bodies.Remove(handle);
                UnregisterBodyForInterpolation(handle);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PhysicsWorld] RemoveBody exception: {ex}");
        }
    }

    public RigidPose GetPose(BodyHandle handle)
    {
        if (_isDisposed) return new RigidPose(NumVec3.Zero);
        lock (_simLock)
        {
            if (!Simulation.Bodies.BodyExists(handle)) return new RigidPose(NumVec3.Zero);
            try { return Simulation.Bodies.GetBodyReference(handle).Pose; } catch { return new RigidPose(NumVec3.Zero); }
        }
    }

    // ---------------------------
    // Raycast helpers
    // ---------------------------
    public bool Raycast(NumVec3 origin, NumVec3 direction, float maxDistance, out BodyHandle hitBody, out NumVec3 hitLocation, out NumVec3 hitNormal, BodyHandle bodyToIgnore = default)
    {
        hitBody = new BodyHandle();
        hitLocation = NumVec3.Zero;
        hitNormal = NumVec3.Zero;
        if (_isDisposed) return false;

        try
        {
            direction = NumVec3.Normalize(direction);
            var handler = new RayHitHandler { BodyToIgnore = bodyToIgnore };
            lock (_simLock)
            {
                Simulation.RayCast(origin, direction, maxDistance, ref handler);
            }

            if (handler.Hit)
            {
                hitBody = handler.Body;
                hitLocation = origin + direction * handler.T;
                hitNormal = handler.Normal;
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PhysicsWorld] Raycast exception: {ex}");
        }
        return false;
    }

    // ---------------------------
    // Shape refcounting
    // ---------------------------
    private void AddShapeRef(TypedIndex idx)
    {
        if (idx.Equals(default(TypedIndex))) return;
        lock (_simLock)
        {
            if (_shapeRefCount.TryGetValue(idx, out var c)) _shapeRefCount[idx] = c + 1;
            else _shapeRefCount[idx] = 1;
        }
    }

    private void ReleaseShapeRef(TypedIndex idx)
    {
        if (idx.Equals(default(TypedIndex))) return;
        lock (_simLock)
        {
            if (!_shapeRefCount.TryGetValue(idx, out var c))
            {
                // Not tracked: attempt safe remove
                try { Simulation.Shapes.Remove(idx); } catch { }
                return;
            }

            if (c <= 1)
            {
                _shapeRefCount.Remove(idx);
                try { Simulation.Shapes.Remove(idx); } catch (Exception ex) { Console.WriteLine($"[PhysicsWorld] ReleaseShapeRef remove exception: {ex}"); }
            }
            else
            {
                _shapeRefCount[idx] = c - 1;
            }
        }
    }

    // ---------------------------
    // Interpolation / tracking helpers (public register)
    // ---------------------------
    public void RegisterBodyForInterpolation(BodyHandle handle)
    {
        if (handle.Equals(default)) return;
        lock (_simLock)
        {
            _trackedBodies.Add(handle);
            try
            {
                if (Simulation.Bodies.BodyExists(handle))
                {
                    var pose = Simulation.Bodies.GetBodyReference(handle).Pose;
                    _currentPoses[handle] = pose;
                    _previousPoses[handle] = pose;
                }
            }
            catch { /* swallow */ }
        }
    }

    public void UnregisterBodyForInterpolation(BodyHandle handle)
    {
        lock (_simLock)
        {
            _trackedBodies.Remove(handle);
            _currentPoses.Remove(handle);
            _previousPoses.Remove(handle);
        }
    }

    private void SnapshotCurrentPosesToPrevious()
    {
        foreach (var kv in _currentPoses)
        {
            _previousPoses[kv.Key] = kv.Value;
        }
    }

    private void UpdateCurrentPosesFromSimulation()
    {
        foreach (var handle in _trackedBodies)
        {
            try
            {
                if (Simulation.Bodies.BodyExists(handle))
                {
                    var pose = Simulation.Bodies.GetBodyReference(handle).Pose;
                    _currentPoses[handle] = pose;
                }
            }
            catch { /* body may have been removed */ }
        }
    }

    // ---------------------------
    // Dispose
    // ---------------------------
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Console.WriteLine("[PhysicsWorld] Disposing...");

        lock (_simLock)
        {
            try { Simulation?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[PhysicsWorld] Exception disposing Simulation: {ex}"); }
            try { _threadDispatcher?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[PhysicsWorld] Exception disposing ThreadDispatcher: {ex}"); }
            try { _bufferPool?.Clear(); } catch (Exception ex) { Console.WriteLine($"[PhysicsWorld] Exception clearing BufferPool: {ex}"); }
        }

        Console.WriteLine("[PhysicsWorld] Disposed.");
    }
}
