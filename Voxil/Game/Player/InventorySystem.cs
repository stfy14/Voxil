using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

public abstract class Item
{
    public string Name { get; protected set; }
    public VoxelObject ViewModel { get; protected set; }
    public virtual void Update(Player player, float dt) { }
    public virtual void OnUse(Player player) { }
}

public abstract class ThrowableItem : Item
{
    protected float ThrowForce = 20.0f;
    protected float Cooldown = 1.0f;
    protected float _currentCooldown = 0f;

    public override void Update(Player player, float dt)
    {
        if (_currentCooldown > 0) _currentCooldown -= dt;
    }

    public override void OnUse(Player player)
    {
        if (_currentCooldown <= 0)
        {
            Throw(player);
            _currentCooldown = Cooldown;
        }
    }

    protected abstract void Throw(Player player);
}

public class EmptyHandItem : Item
{
    private readonly IVoxelEditService _editService;
    private readonly IWorldService _worldService;
    private float _breakCooldown = 0f;

    public EmptyHandItem()
    {
        _editService  = ServiceLocator.Get<IVoxelEditService>();
        _worldService = ServiceLocator.Get<IWorldService>();
        Name          = "Hand";
        ViewModel     = null;
    }

    public override void Update(Player player, float dt)
    {
        if (_breakCooldown > 0) _breakCooldown -= dt;
    }

    public override void OnUse(Player player)
    {
        if (_breakCooldown > 0) return;

        var physics = _worldService.PhysicsWorld;
        var pos = player.Camera.Position.ToSystemNumerics();
        var dir = player.Camera.Front.ToSystemNumerics();

        var hit = new VoxelHitHandler
        {
            PlayerBodyHandle = physics.GetPlayerState().BodyHandle,
            Simulation       = physics.Simulation
        };
        physics.Simulation.RayCast(pos, dir, 5.0f, physics.Simulation.BufferPool, ref hit);

        if (hit.Hit)
        {
            _editService.DestroyVoxelAt(hit.Collidable, pos + dir * hit.T, hit.Normal);
            _breakCooldown = 0.2f;
        }
    }
}

public class DynamiteItem : ThrowableItem
{
    private readonly IWorldService _worldService;

    public DynamiteItem()
    {
        _worldService = ServiceLocator.Get<IWorldService>();
        Name          = "TNT";
        ThrowForce    = 15.0f;
        Cooldown      = 0.5f;
        ViewModel     = new VoxelObject(DynamiteEntity.GetDynamiteShape(), MaterialType.TNT, 0.15f);
    }

    protected override void Throw(Player player)
    {
        var camPos = player.Camera.Position;
        var camDir = player.Camera.Front;

        var spawnOffset = camDir * 1.5f - new Vector3(0, 0.3f, 0);
        var spawnPos    = camPos + spawnOffset;

        var playerVelocity = _worldService.GetPlayerVelocity();
        var throwVelocity  = camDir.ToSystemNumerics() * ThrowForce + playerVelocity;

        new DynamiteEntity(spawnPos.ToSystemNumerics(), throwVelocity);
    }
}