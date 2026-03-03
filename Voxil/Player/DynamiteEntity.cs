using BepuPhysics;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;

public class DynamiteEntity
{
    private readonly WorldManager _worldManager;
    private readonly VoxelObject _voxelObject;
    private float _timer = 3.0f; // Время до взрыва
    private bool _exploded = false;

    public bool IsDead => _exploded;

    public DynamiteEntity(WorldManager wm, System.Numerics.Vector3 position, System.Numerics.Vector3 velocity)
    {
        _worldManager = wm;

        // ИСПОЛЬЗУЕМ ЕДИНУЮ ФОРМУ И МАСШТАБ 0.15f
        _voxelObject = new VoxelObject(GetDynamiteShape(), MaterialType.TNT, 0.15f); 
    
        _worldManager.SpawnDynamicObject(_voxelObject, position, velocity);
        Game.RegisterEntity(this); 
    }
    
    public static List<Vector3i> GetDynamiteShape()
    {
        var voxels = new List<Vector3i>();
    
        // Тело шашки (толщина 2x2, высота 3)
        for (int y = 0; y < 3; y++)
        {
            voxels.Add(new Vector3i(0, y, 0));
            voxels.Add(new Vector3i(1, y, 0));
            voxels.Add(new Vector3i(0, y, 1));
            voxels.Add(new Vector3i(1, y, 1));
        }
    
        // Фитиль
        voxels.Add(new Vector3i(0, 3, 0));
    
        return voxels;
    }

    public void Update(float dt)
    {
        if (_exploded) return;

        _timer -= dt;
        if (_timer <= 0)
        {
            Explode();
        }
    }

    private void Explode()
    {
        _exploded = true;
        var pos = _voxelObject.Position;

        Console.WriteLine("BOOM!");

        // 1. Удаляем сам динамит
        _worldManager.DestroyVoxelObject(_voxelObject);

        // 2. Взрыв вокселей мира (Радиус 4 блока)
        ExplosionSystem.CreateExplosion(_worldManager, pos, 4.0f, 200.0f);
    }
}