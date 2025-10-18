// /Physics/PhysicsWorld.cs
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

    // НОВОЕ: Храним ссылки на коллбэки
    private NarrowPhaseCallbacks _narrowPhaseCallbacks;
    private PoseIntegratorCallbacks _poseIntegratorCallbacks;

    public PhysicsWorld()
    {
        _bufferPool = new BufferPool();
        int threadCount = Math.Max(1, Environment.ProcessorCount > 4 ? Environment.ProcessorCount - 2 : Environment.ProcessorCount - 1);
        _threadDispatcher = new ThreadDispatcher(threadCount);

        _narrowPhaseCallbacks = new NarrowPhaseCallbacks();
        _poseIntegratorCallbacks = new PoseIntegratorCallbacks();
        _narrowPhaseCallbacks.Initialize(null);
        _poseIntegratorCallbacks.Initialize(null);

        var solveDescription = new SolveDescription(12, 2);
        Simulation = Simulation.Create(_bufferPool, _narrowPhaseCallbacks, _poseIntegratorCallbacks, solveDescription);

        Console.WriteLine("[PhysicsWorld] Физический мир инициализирован.");
    }

    // НОВЫЙ МЕТОД: Устанавливаем игрока (вызывается один раз после создания PlayerController)
    public void SetPlayerHandle(BodyHandle playerHandle)
    {
        _narrowPhaseCallbacks.PlayerHandle = playerHandle;
        _poseIntegratorCallbacks.PlayerHandle = playerHandle;
        Console.WriteLine($"[PhysicsWorld] PlayerHandle установлен: {playerHandle.Value}");
    }

    public void Update(float deltaTime) => Simulation.Timestep(deltaTime);

    public StaticHandle CreateStaticVoxelBody(Vector3 position, IList<OpenTK.Mathematics.Vector3i> voxelCoordinates)
    {
        if (voxelCoordinates.Count == 0)
            return new StaticHandle();

        var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxelCoordinates.Count);
        try
        {
            var boxShape = new Box(1, 1, 1);
            foreach (var coord in voxelCoordinates)
            {
                var pose = new RigidPose(new Vector3(coord.X, coord.Y, coord.Z));
                compoundBuilder.Add(boxShape, pose, 1);
            }

            compoundBuilder.BuildKinematicCompound(out var children);
            var compound = new Compound(children);
            var shapeIndex = Simulation.Shapes.Add(compound);

            var staticDescription = new StaticDescription(position, shapeIndex);
            return Simulation.Statics.Add(staticDescription);
        }
        finally
        {
            compoundBuilder.Dispose();
        }
    }

    public BodyHandle CreateVoxelObjectBody(IList<OpenTK.Mathematics.Vector3i> voxelCoordinates, MaterialType materialType, Vector3 initialPosition, out Vector3 localCenterOfMass)
    {
        if (voxelCoordinates.Count == 0)
        {
            localCenterOfMass = Vector3.Zero;
            return new BodyHandle();
        }

        var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxelCoordinates.Count);
        try
        {
            float voxelMass = MaterialRegistry.Get(materialType).MassPerVoxel;
            var boxShape = new Box(1, 1, 1);

            foreach (var coord in voxelCoordinates)
            {
                var pose = new RigidPose(new Vector3(coord.X, coord.Y, coord.Z));
                compoundBuilder.Add(boxShape, pose, voxelMass);
            }

            compoundBuilder.BuildDynamicCompound(out var children, out var inertia, out localCenterOfMass);
            var compound = new Compound(children);
            var shapeIndex = Simulation.Shapes.Add(compound);

            var bodyDescription = BodyDescription.CreateDynamic(
                new RigidPose(initialPosition), inertia,
                new CollidableDescription(shapeIndex, 0.1f),
                new BodyActivityDescription(0.01f));

            return Simulation.Bodies.Add(bodyDescription);
        }
        finally
        {
            compoundBuilder.Dispose();
        }
    }

    public BodyHandle UpdateVoxelObjectBody(BodyHandle handle, IList<OpenTK.Mathematics.Vector3i> voxelCoordinates, MaterialType materialType, out Vector3 localCenterOfMass)
    {
        if (voxelCoordinates.Count == 0)
        {
            localCenterOfMass = Vector3.Zero;
            RemoveBody(handle);
            return new BodyHandle();
        }

        var bodyReference = Simulation.Bodies.GetBodyReference(handle);

        var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxelCoordinates.Count);
        try
        {
            float voxelMass = MaterialRegistry.Get(materialType).MassPerVoxel;
            var boxShape = new Box(1, 1, 1);

            foreach (var coord in voxelCoordinates)
            {
                var pose = new RigidPose(new Vector3(coord.X, coord.Y, coord.Z));
                compoundBuilder.Add(boxShape, pose, voxelMass);
            }

            compoundBuilder.BuildDynamicCompound(out var children, out var inertia, out localCenterOfMass);
            var compound = new Compound(children);
            var newShapeIndex = Simulation.Shapes.Add(compound);

            bodyReference.SetShape(newShapeIndex);
            bodyReference.LocalInertia = inertia;

            Simulation.Shapes.Remove(bodyReference.Collidable.Shape);

            return handle;
        }
        finally
        {
            compoundBuilder.Dispose();
        }
    }

    public void RemoveBody(BodyHandle handle)
    {
        if (Simulation.Bodies.BodyExists(handle))
            Simulation.Bodies.Remove(handle);
    }

    public RigidPose GetPose(BodyHandle handle)
    {
        return Simulation.Bodies.GetBodyReference(handle).Pose;
    }

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out BodyHandle hitBody, out Vector3 hitLocation, out Vector3 hitNormal, BodyHandle bodyToIgnore = default)
    {
        direction = Vector3.Normalize(direction);

        var hitHandler = new RayHitHandler
        {
            BodyToIgnore = bodyToIgnore
        };

        Simulation.RayCast(origin, direction, maxDistance, ref hitHandler);

        if (hitHandler.Hit)
        {
            hitBody = hitHandler.Body;
            hitLocation = origin + direction * hitHandler.T;
            hitNormal = hitHandler.Normal;
            return true;
        }

        hitBody = new BodyHandle();
        hitLocation = default;
        hitNormal = default;
        return false;
    }

    public void Dispose()
    {
        Simulation.Dispose();
        _threadDispatcher.Dispose();
        _bufferPool.Clear();
        Console.WriteLine("[PhysicsWorld] Физический мир уничтожен.");
    }
}