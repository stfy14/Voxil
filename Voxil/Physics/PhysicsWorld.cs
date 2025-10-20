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
    private readonly PlayerState _playerState = new PlayerState();

    public PhysicsWorld()
    {
        _bufferPool = new BufferPool();
        _threadDispatcher = new ThreadDispatcher(Math.Max(1, Environment.ProcessorCount > 4 ? Environment.ProcessorCount - 2 : Environment.ProcessorCount - 1));

        var narrowPhaseCallbacks = new NarrowPhaseCallbacks { PlayerState = _playerState };
        var poseIntegratorCallbacks = new PoseIntegratorCallbacks { PlayerState = _playerState, World = this };

        poseIntegratorCallbacks.Initialize(null);
        var solveDescription = new SolveDescription(12, 2);
        Simulation = Simulation.Create(_bufferPool, narrowPhaseCallbacks, poseIntegratorCallbacks, solveDescription);
        Console.WriteLine("[PhysicsWorld] Физический мир инициализирован.");
    }

    public void SetPlayerHandle(BodyHandle playerHandle)
    {
        _playerState.BodyHandle = playerHandle;
        Console.WriteLine($"[PhysicsWorld] PlayerHandle установлен в общем состоянии: {playerHandle.Value}");
    }

    public void SetPlayerGoalVelocity(Vector2 goalVelocity)
    {
        _playerState.GoalVelocity = goalVelocity;
    }

    public PlayerState GetPlayerState() => _playerState;

    public void Update(float deltaTime) => Simulation.Timestep(deltaTime);

    public StaticHandle CreateStaticVoxelBody(Vector3 position, IList<OpenTK.Mathematics.Vector3i> voxelCoordinates)
    {
        // ЗАЩИТА: Проверяем null и пустой список
        if (voxelCoordinates == null || voxelCoordinates.Count == 0)
        {
            Console.WriteLine("[PhysicsWorld] ОШИБКА: Попытка создать статическое тело с null/пустым списком вокселей!");
            return new StaticHandle();
        }

        var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxelCoordinates.Count);
        try
        {
            var boxShape = new Box(1, 1, 1);

            // ЗАЩИТА: try-catch на случай, если voxelCoordinates изменился во время итерации
            try
            {
                foreach (var coord in voxelCoordinates)
                {
                    var pose = new RigidPose(new Vector3(coord.X, coord.Y, coord.Z));
                    compoundBuilder.Add(boxShape, pose, 1);
                }
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"[PhysicsWorld] Коллекция вокселей изменилась во время создания тела: {ex.Message}");
                return new StaticHandle();
            }

            compoundBuilder.BuildKinematicCompound(out var children);

            if (children.Length == 0)
            {
                Console.WriteLine("[PhysicsWorld] ОШИБКА: BuildKinematicCompound вернул 0 дочерних объектов!");
                return new StaticHandle();
            }

            var compound = new Compound(children);
            var shapeIndex = Simulation.Shapes.Add(compound);

            if (!shapeIndex.Exists)
            {
                Console.WriteLine("[PhysicsWorld] ОШИБКА: Не удалось добавить форму (возможно, переполнение пула)");
                _bufferPool.Return(ref children);
                return new StaticHandle();
            }

            var staticDescription = new StaticDescription(position, shapeIndex);
            var staticHandle = Simulation.Statics.Add(staticDescription);

            return staticHandle;
        }
        finally
        {
            compoundBuilder.Dispose();
        }
    }

    public BodyHandle CreateVoxelObjectBody(IList<OpenTK.Mathematics.Vector3i> voxelCoordinates, MaterialType materialType, Vector3 initialPosition, out Vector3 localCenterOfMass)
    {
        if (voxelCoordinates.Count == 0) { localCenterOfMass = Vector3.Zero; return new BodyHandle(); }
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
            var bodyDescription = BodyDescription.CreateDynamic(new RigidPose(initialPosition), inertia, new CollidableDescription(shapeIndex, 0.1f), new BodyActivityDescription(0.01f));
            return Simulation.Bodies.Add(bodyDescription);
        }
        finally { compoundBuilder.Dispose(); }
    }

    public BodyHandle UpdateVoxelObjectBody(BodyHandle handle, IList<OpenTK.Mathematics.Vector3i> voxelCoordinates, MaterialType materialType, out Vector3 localCenterOfMass)
    {
        if (voxelCoordinates.Count == 0) { localCenterOfMass = Vector3.Zero; RemoveBody(handle); return new BodyHandle(); }
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
        finally { compoundBuilder.Dispose(); }
    }

    public void RemoveBody(BodyHandle handle) { if (Simulation.Bodies.BodyExists(handle)) Simulation.Bodies.Remove(handle); }
    public RigidPose GetPose(BodyHandle handle) { return Simulation.Bodies.GetBodyReference(handle).Pose; }

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out BodyHandle hitBody, out Vector3 hitLocation, out Vector3 hitNormal, BodyHandle bodyToIgnore = default)
    {
        direction = Vector3.Normalize(direction);
        var hitHandler = new RayHitHandler { BodyToIgnore = bodyToIgnore };
        Simulation.RayCast(origin, direction, maxDistance, ref hitHandler);
        if (hitHandler.Hit) { hitBody = hitHandler.Body; hitLocation = origin + direction * hitHandler.T; hitNormal = hitHandler.Normal; return true; }
        hitBody = new BodyHandle(); hitLocation = default; hitNormal = default; return false;
    }

    public void Dispose() { Simulation.Dispose(); _threadDispatcher.Dispose(); _bufferPool.Clear(); Console.WriteLine("[PhysicsWorld] Физический мир уничтожен."); }
}