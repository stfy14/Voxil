// PhysicsWorld.cs

using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;

public class PhysicsWorld : IDisposable
{
    public Simulation Simulation { get; private set; }
    private readonly BufferPool _bufferPool;
    private readonly ThreadDispatcher _threadDispatcher;
    private readonly NarrowPhaseCallbacks _narrowPhaseCallbacks;
    private readonly PoseIntegratorCallbacks _poseIntegratorCallbacks;

    public PhysicsWorld()
    {
        _bufferPool = new BufferPool();
        int threadCount = Math.Max(1, Environment.ProcessorCount > 4 ? Environment.ProcessorCount - 2 : Environment.ProcessorCount - 1);
        _threadDispatcher = new ThreadDispatcher(threadCount);

        _narrowPhaseCallbacks = new NarrowPhaseCallbacks { SpringSettings = new SpringSettings(30, 1) };
        _poseIntegratorCallbacks = new PoseIntegratorCallbacks { Gravity = new Vector3(0, -10, 0) };

        var solveDescription = new SolveDescription(8, 1);

        Simulation = Simulation.Create(_bufferPool, _narrowPhaseCallbacks, _poseIntegratorCallbacks, solveDescription);

        // --- ИСПРАВЛЕНО ЗДЕСЬ ---
        // Чтобы создать статическое тело, нужно просто создать BodyDescription
        // БЕЗ инерции (LocalInertia). Движок сам поймет, что тело статично.
        var floorShape = new Box(500, 1, 500);
        var staticBodyDesc = new BodyDescription
        {
            Pose = new RigidPose(new Vector3(0, -5, 0)),
            Collidable = new CollidableDescription(Simulation.Shapes.Add(floorShape), 0.1f)
            // Поле LocalInertia не задано, поэтому тело будет иметь бесконечную массу (статично).
        };
        Simulation.Bodies.Add(staticBodyDesc);
        // -------------------------

        Console.WriteLine("[PhysicsWorld] Физический мир инициализирован.");
    }

    public void Update(float deltaTime) => Simulation.Timestep(deltaTime);

    public BodyHandle CreateVoxelObjectBody(System.Collections.Generic.List<OpenTK.Mathematics.Vector3i> voxelCoordinates, Vector3 initialPosition, out Vector3 localCenterOfMass)
    {
        if (voxelCoordinates.Count == 0)
        {
            localCenterOfMass = Vector3.Zero;
            return new BodyHandle();
        }

        var compoundBuilder = new CompoundBuilder(_bufferPool, Simulation.Shapes, voxelCoordinates.Count);
        try
        {
            var boxShape = new Box(1, 1, 1);
            foreach (var coord in voxelCoordinates)
            {
                var pose = new RigidPose(new Vector3(coord.X, coord.Y, coord.Z));
                compoundBuilder.Add(boxShape, pose, 1);
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

    public void RemoveBody(BodyHandle handle)
    {
        if (Simulation.Bodies.BodyExists(handle))
            Simulation.Bodies.Remove(handle);
    }

    public RigidPose GetPose(BodyHandle handle) => Simulation.Bodies[handle].Pose;

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out BodyHandle hitBody, out Vector3 hitLocation, out Vector3 hitNormal)
    {
        var hitHandler = new RayHitHandler();
        Simulation.RayCast(origin, direction, maxDistance, ref hitHandler);
        if (hitHandler.Hit)
        {
            hitBody = hitHandler.Body;
            hitLocation = origin + direction * hitHandler.T;
            hitNormal = hitHandler.Normal; // <-- ДОБАВЛЕНО
            return true;
        }
        hitBody = new BodyHandle();
        hitLocation = default;
        hitNormal = default; // <-- ДОБАВЛЕНО
        return false;
    }

    public void Dispose()
    {
        Simulation.Dispose();
        _threadDispatcher.Dispose();
        _bufferPool.Clear();
        Console.WriteLine("[PhysicsWorld] Физический мир уничтожен.");
    }

    // ... (Код коллбэков NarrowPhaseCallbacks и PoseIntegratorCallbacks остается без изменений) ...

    private struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
    {
        public SpringSettings SpringSettings;
        public void Initialize(Simulation simulation) { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin) => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterialProperties) where TManifold : unmanaged, IContactManifold<TManifold>
        {
            pairMaterialProperties = new PairMaterialProperties { FrictionCoefficient = 1f, MaximumRecoveryVelocity = 2f, SpringSettings = this.SpringSettings };
            return true;
        }
        public void Dispose() { }
    }

    private struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
    {
        public Vector3 Gravity;
        private Vector3Wide GravityWide;
        public void Initialize(Simulation simulation) { }
        public void PrepareForIntegration(float dt) { GravityWide = Vector3Wide.Broadcast(Gravity); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity) { velocity.Linear += GravityWide * dt; }
        public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
        public readonly bool AllowSubstepsForUnconstrainedBodies => false;
        public readonly bool IntegrateVelocityForKinematics => false;
    }
    private struct RayHitHandler : IRayHitHandler
    {
        public bool Hit;
        public float T;
        public BodyHandle Body;
        public Vector3 Normal; // <-- ДОБАВЛЕНО

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
        {
            if (t < maximumT)
            {
                maximumT = t;
                Hit = true;
                T = t;
                Body = collidable.BodyHandle;
                Normal = normal; // <-- ДОБАВЛЕНО
            }
        }
    }
}