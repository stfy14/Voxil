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

// === ИСПРАВЛЕННАЯ ПУСТАЯ РУКА ===
public class EmptyHandItem : Item
{
    private readonly WorldManager _worldManager;
    private float _breakCooldown = 0f;

    public EmptyHandItem(WorldManager wm)
    {
        _worldManager = wm;
        Name = "Hand";
        ViewModel = null;
    }

    public override void Update(Player player, float dt)
    {
        if (_breakCooldown > 0) _breakCooldown -= dt;
    }

    public override void OnUse(Player player)
    {
        if (_breakCooldown > 0) return;

        // Логика рейкаста (взята из Game.cs)
        var physics = _worldManager.PhysicsWorld;
        var pos = player.Camera.Position.ToSystemNumerics();
        var dir = player.Camera.Front.ToSystemNumerics();
        
        var hit = new VoxelHitHandler { PlayerBodyHandle = physics.GetPlayerState().BodyHandle, Simulation = physics.Simulation };
        physics.Simulation.RayCast(pos, dir, 5.0f, physics.Simulation.BufferPool, ref hit); // Дистанция 5 метров

        if (hit.Hit)
        {
            // Ломаем блок
            _worldManager.DestroyVoxelAt(hit.Collidable, pos + dir * hit.T, hit.Normal);
            _breakCooldown = 0.2f; // Задержка между ударами
        }
    }
}

public class DynamiteItem : ThrowableItem
{
    private readonly WorldManager _worldManager;

    public DynamiteItem(WorldManager wm)
    {
        _worldManager = wm;
        Name = "TNT";
        ThrowForce = 15.0f;
        Cooldown = 0.5f;

        // ИСПОЛЬЗУЕМ ТУ ЖЕ САМУЮ ФОРМУ И ТОТ ЖЕ МАСШТАБ 0.15f
        ViewModel = new VoxelObject(DynamiteEntity.GetDynamiteShape(), MaterialType.TNT, 0.15f);
    }

    protected override void Throw(Player player)
    {
        var camPos = player.Camera.Position;
        var camDir = player.Camera.Front;

        // 1. Отодвигаем спавн дальше (1.5 метра от глаз)
        // 2. Опускаем ниже (-0.3 метра), чтобы летело "от руки", а не из глаз
        var spawnOffset = camDir * 1.5f - new OpenTK.Mathematics.Vector3(0, 0.3f, 0);
        var spawnPos = camPos + spawnOffset;

        // Добавляем инерцию игрока (чтобы на бегу не врезаться в свой же динамит)
        var playerVelocity = _worldManager.GetPlayerVelocity();
        var throwVelocity = camDir.ToSystemNumerics() * ThrowForce + playerVelocity;

        var tntEntity = new DynamiteEntity(_worldManager, spawnPos.ToSystemNumerics(), throwVelocity);
    }
}