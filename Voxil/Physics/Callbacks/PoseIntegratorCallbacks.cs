// /Physics/Callbacks/PoseIntegratorCallbacks.cs
using BepuPhysics;
using BepuUtilities;
using System.Numerics;
using System.Runtime.CompilerServices;

public struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    public Vector3 Gravity;
    private Vector3Wide GravityWide;
    public BodyHandle PlayerHandle;

    public void Initialize(Simulation simulation)
    {
        Gravity = new Vector3(0, -20, 0);
    }

    public void PrepareForIntegration(float dt)
    {
        GravityWide = Vector3Wide.Broadcast(Gravity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
    {
        // КРИТИЧНО: НЕ применяем гравитацию к игроку!
        // Гравитация для игрока управляется вручную в PlayerController

        // Для других динамических объектов применяем гравитацию
        // (в BepuPhysics нет прямого способа проверить конкретный BodyHandle в векторизованном коде,
        // поэтому применяем ко всем, но игрок все равно переписывает свою скорость)
        velocity.Linear += GravityWide * dt;
    }

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;
}