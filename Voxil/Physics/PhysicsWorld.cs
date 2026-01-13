// --- START OF FILE PhysicsWorld.cs ---
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using BepuUtilities.Memory;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq; // Важно для Min/Max
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

    private const float SimulationTimestep = 1f / 60f;
    private float _timeAccumulator = 0f;
    private Stopwatch _stopwatch = new Stopwatch();

    // --- НОВОЕ СВОЙСТВО: Фактор интерполяции (0.0 .. 1.0) ---
    public float PhysicsAlpha { get; private set; } = 0f;

    public PhysicsWorld()
    {
        _bufferPool = new BufferPool();
        int threadCount = Math.Max(1, Environment.ProcessorCount - 2);
        _threadDispatcher = new ThreadDispatcher(threadCount);

        var narrowPhaseCallbacks = new NarrowPhaseCallbacks { PlayerState = _player_state };
        var poseIntegratorCallbacks = new PoseIntegratorCallbacks { PlayerState = _player_state };
        var solveDescription = new SolveDescription(8, 1);

        Simulation = Simulation.Create(_bufferPool, narrowPhaseCallbacks, poseIntegratorCallbacks, solveDescription);
    }
    
    // ... (Методы SetThreadCount, AddStaticChunkBody, RemoveStaticBody, ReturnCompoundBuffer остаются без изменений) ...
    public void SetThreadCount(int count)
    {
        lock (_simLock)
        {
            if (_threadDispatcher != null && _threadDispatcher.ThreadCount == count) return;
            var oldDispatcher = _threadDispatcher;
            _threadDispatcher = new ThreadDispatcher(count);
            oldDispatcher?.Dispose();
        }
    }

    public StaticHandle AddStaticChunkBody(BepuVector3 chunkWorldPosition, VoxelCollider[] colliders, int count)
    {
        lock (_simLock)
        {
            if (count == 0) return new StaticHandle();
            TypedIndex shapeIndex;
            if (count == 1)
            {
                var c = colliders[0];
                var boxShape = new Box(c.HalfSize.X * 2, c.HalfSize.Y * 2, c.HalfSize.Z * 2);
                shapeIndex = Simulation.Shapes.Add(boxShape);
                var finalPos = chunkWorldPosition + c.Position;
                return Simulation.Statics.Add(new StaticDescription(finalPos, shapeIndex));
            }
            else
            {
                _bufferPool.Take<CompoundChild>(count, out var children);
                for (int i = 0; i < count; i++)
                {
                    var c = colliders[i];
                    var boxShape = new Box(c.HalfSize.X * 2, c.HalfSize.Y * 2, c.HalfSize.Z * 2);
                    var boxIndex = Simulation.Shapes.Add(boxShape);
                    children[i] = new CompoundChild { ShapeIndex = boxIndex, LocalPosition = c.Position, LocalOrientation = BepuQuaternion.Identity };
                }
                var compoundShape = new Compound(children.Slice(0, count));
                shapeIndex = Simulation.Shapes.Add(compoundShape);
                lock (_bufferLock) _compoundBuffers.Add(shapeIndex, children);
                return Simulation.Statics.Add(new StaticDescription(chunkWorldPosition, shapeIndex));
            }
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
                bool isCompound = false;
                lock (_bufferLock) isCompound = _compoundBuffers.ContainsKey(shapeIndex);
                if (isCompound)
                {
                    var compoundShape = Simulation.Shapes.GetShape<Compound>(shapeIndex.Index);
                    for (int i = 0; i < compoundShape.ChildCount; i++) Simulation.Shapes.Remove(compoundShape.Children[i].ShapeIndex);
                    ReturnCompoundBuffer(shapeIndex);
                }
                Simulation.Shapes.Remove(shapeIndex);
                Simulation.Statics.Remove(handle);
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

    // ✅ ВРЕМЕННЫЙ WORKAROUND: Ручной расчёт CoM

public BodyHandle CreateVoxelObjectBody(IList<Vector3i> voxelCoordinates, MaterialType material, BepuVector3 initialPosition, out BepuVector3 localCenterOfMass)
{
    lock (_simLock)
    {
        localCenterOfMass = BepuVector3.Zero;
        if (_isDisposed || voxelCoordinates == null || voxelCoordinates.Count == 0) return new BodyHandle();

        // 1. Границы объекта
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        foreach (var v in voxelCoordinates)
        {
            if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
            if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
        }

        int sizeX = maxX - minX + 1;
        int sizeY = maxY - minY + 1;
        int sizeZ = maxZ - minZ + 1;

        // 2. Greedy Meshing
        var voxels = System.Buffers.ArrayPool<MaterialType>.Shared.Rent(sizeX * sizeY * sizeZ);
        var visited = System.Buffers.ArrayPool<bool>.Shared.Rent(sizeX * sizeY * sizeZ);
        var scratchColliders = System.Buffers.ArrayPool<VoxelCollider>.Shared.Rent(voxelCoordinates.Count);

        Array.Clear(voxels, 0, sizeX * sizeY * sizeZ);

        foreach (var v in voxelCoordinates)
        {
            int lx = v.X - minX;
            int ly = v.Y - minY;
            int lz = v.Z - minZ;
            int idx = lx + sizeX * (ly + sizeY * lz);
            voxels[idx] = material;
        }

        int colliderCount = VoxelPhysicsBuilder.GenerateColliders(voxels, visited, scratchColliders, sizeX, sizeY, sizeZ);

        System.Buffers.ArrayPool<MaterialType>.Shared.Return(voxels);
        System.Buffers.ArrayPool<bool>.Shared.Return(visited);

        // 3. ✅ Ручной расчёт центра масс (workaround для бага в CompoundBuilder)
        BepuVector3 manualCoM = BepuVector3.Zero;
        foreach (var v in voxelCoordinates)
        {
            manualCoM += new BepuVector3(
                (v.X + 0.5f) * Constants.VoxelSize,
                (v.Y + 0.5f) * Constants.VoxelSize,
                (v.Z + 0.5f) * Constants.VoxelSize
            );
        }
        manualCoM /= voxelCoordinates.Count;

        // 4. Compound Shape
        using (var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, colliderCount))
        {
            float voxelMass = MaterialRegistry.Get(material).MassPerVoxel;

            for(int i = 0; i < colliderCount; i++)
            {
                var col = scratchColliders[i];
                var boxShape = new Box(col.HalfSize.X * 2, col.HalfSize.Y * 2, col.HalfSize.Z * 2);
                
                float volumeInVoxels = (col.HalfSize.X * 2 * col.HalfSize.Y * 2 * col.HalfSize.Z * 2) / (float)Math.Pow(Constants.VoxelSize, 3);
                float boxMass = voxelMass * volumeInVoxels;

                compoundBuilder.Add(boxShape, new RigidPose(col.Position), boxMass);
            }
            
            System.Buffers.ArrayPool<VoxelCollider>.Shared.Return(scratchColliders);

            compoundBuilder.BuildDynamicCompound(out Buffer<CompoundChild> childrenBuffer, out BodyInertia inertia, out BepuVector3 _);
            
            // Используем ручной расчёт CoM вместо значения из компаунда
            localCenterOfMass = manualCoM;
            
            if (childrenBuffer.Length == 0)
            {
                if (childrenBuffer.Allocated) _bufferPool.Return(ref childrenBuffer);
                return new BodyHandle();
            }

            var compound = new Compound(childrenBuffer);
            var compoundShapeIndex = Simulation.Shapes.Add(compound);
            lock (_bufferLock) { _compoundBuffers.Add(compoundShapeIndex, childrenBuffer); }

            var bodyDescription = BodyDescription.CreateDynamic(new RigidPose(initialPosition), inertia, new CollidableDescription(compoundShapeIndex, 0.1f), new BodyActivityDescription(0.01f));
            return Simulation.Bodies.Add(bodyDescription);
        }
    }
}

    public BodyHandle UpdateVoxelObjectBody(BodyHandle oldHandle, IList<Vector3i> voxelCoordinates, MaterialType material, out BepuVector3 localCenterOfMass)
    {
        localCenterOfMass = BepuVector3.Zero;
        lock (_simLock)
        {
            if (_isDisposed) return new BodyHandle();
            RigidPose oldPose = new RigidPose(BepuVector3.Zero);
            BodyVelocity oldVel = new BodyVelocity();
            bool hasOldBody = false;

            if (Simulation.Bodies.BodyExists(oldHandle))
            {
                var bodyRef = Simulation.Bodies.GetBodyReference(oldHandle);
                oldPose = bodyRef.Pose;
                oldVel = bodyRef.Velocity;
                hasOldBody = true;
                RemoveBody(oldHandle);
            }

            var newHandle = CreateVoxelObjectBody(voxelCoordinates, material, hasOldBody ? oldPose.Position : BepuVector3.Zero, out localCenterOfMass);

            if (Simulation.Bodies.BodyExists(newHandle) && hasOldBody)
            {
                var newBodyRef = Simulation.Bodies.GetBodyReference(newHandle);
                newBodyRef.Velocity = oldVel;
                newBodyRef.Pose.Orientation = oldPose.Orientation;
            }
            return newHandle;
        }
    }
    
    // ... (Методы RemoveBody, GetPose, SetPlayerHandle, GetPlayerState и т.д. остаются без изменений) ...
    public void RemoveBody(BodyHandle handle) { if (_isDisposed) return; lock (_simLock) { try { if (Simulation.Bodies.BodyExists(handle)) { var bodyReference = Simulation.Bodies.GetBodyReference(handle); var shapeIndex = bodyReference.Collidable.Shape; Simulation.Bodies.Remove(handle); bool isCompound = false; lock(_bufferLock) isCompound = _compoundBuffers.ContainsKey(shapeIndex); if(isCompound) { var compoundShape = Simulation.Shapes.GetShape<Compound>(shapeIndex.Index); for (int i = 0; i < compoundShape.ChildCount; i++) { Simulation.Shapes.Remove(compoundShape.Children[i].ShapeIndex); } ReturnCompoundBuffer(shapeIndex); } Simulation.Shapes.Remove(shapeIndex); } } catch (Exception ex) { Console.WriteLine($"[PhysicsWorld] Error removing body: {ex.Message}"); } } }
    public RigidPose GetPose(BodyHandle handle) { if (_isDisposed) return new RigidPose(BepuVector3.Zero); lock (_simLock) { if (!Simulation.Bodies.BodyExists(handle)) return new RigidPose(BepuVector3.Zero); return Simulation.Bodies.GetBodyReference(handle).Pose; } }
    public void SetPlayerHandle(BodyHandle playerHandle) => _player_state.BodyHandle = playerHandle;
    public void SetPlayerGoalVelocity(System.Numerics.Vector2 goalVelocity) => _player_state.GoalVelocity = goalVelocity;
    public PlayerState GetPlayerState() => _player_state;

    public void Update(float deltaTime)
    {
        if (_isDisposed) return;
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

            // --- РАСЧЕТ ИНТЕРПОЛЯЦИИ ---
            // Alpha показывает, насколько далеко мы продвинулись между физическими кадрами.
            PhysicsAlpha = _timeAccumulator / SimulationTimestep;
        }

        if (PerformanceMonitor.IsEnabled)
        {
            _stopwatch.Stop();
            PerformanceMonitor.Record(ThreadType.Physics, _stopwatch.ElapsedTicks);
        }
    }
    
    private void UpdateCharacterController() { if (!Simulation.Bodies.BodyExists(_player_state.BodyHandle)) return; var bodyReference = Simulation.Bodies.GetBodyReference(_player_state.BodyHandle); var settings = _player_state.Settings; var bodyPosition = bodyReference.Pose.Position; var hitHandler = new RayHitHandler { BodyToIgnore = _player_state.BodyHandle }; float rayLength = PlayerController.Height / 2f + settings.HoverHeight + 0.2f; Simulation.RayCast(bodyPosition, -BepuVector3.UnitY, rayLength, _bufferPool, ref hitHandler); if (hitHandler.Hit) { _player_state.IsOnGround = true; _player_state.RayT = hitHandler.T; } else { _player_state.IsOnGround = false; _player_state.RayT = float.MaxValue; } }
    public bool Raycast(BepuVector3 origin, BepuVector3 direction, float maxDistance, out BodyHandle hitBody, out BepuVector3 hitLocation, out BepuVector3 hitNormal, BodyHandle bodyToIgnore = default) { hitBody = new BodyHandle(); hitLocation = default; hitNormal = default; if (_isDisposed) return false; try { direction = BepuVector3.Normalize(direction); var hitHandler = new RayHitHandler { BodyToIgnore = bodyToIgnore }; lock (_simLock) { Simulation.RayCast(origin, direction, maxDistance, _bufferPool, ref hitHandler); } if (hitHandler.Hit) { hitBody = hitHandler.Body; hitLocation = origin + direction * hitHandler.T; hitNormal = hitHandler.Normal; return true; } } catch (Exception ex) { Console.WriteLine($"[PhysicsWorld] Raycast error: {ex.Message}"); } return false; }
    public void Dispose() { if (_isDisposed) return; _isDisposed = true; lock (_simLock) { Simulation?.Dispose(); } _threadDispatcher?.Dispose(); _bufferPool?.Clear(); }
}