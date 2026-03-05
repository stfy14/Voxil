using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

public class DynamiteEntity : IEntity
{
    private readonly IVoxelObjectService _objectService;
    private readonly IWorldService _worldService;
    private readonly VoxelObject _voxelObject;
    private float _timer = 3.0f;
    private bool _exploded = false;

    public bool IsDead => _exploded;

    public DynamiteEntity(System.Numerics.Vector3 position, System.Numerics.Vector3 velocity)
    {
        _objectService = ServiceLocator.Get<IVoxelObjectService>();
        _worldService  = ServiceLocator.Get<IWorldService>();

        _voxelObject = new VoxelObject(GetDynamiteShape(), MaterialType.TNT, 0.15f);
        _objectService.SpawnDynamicObject(_voxelObject, position, velocity);

        ServiceLocator.Get<EntityManager>().Register(this);
    }

    public static List<Vector3i> GetDynamiteShape()
    {
        var voxels = new List<Vector3i>();

        for (int y = 0; y < 3; y++)
        {
            voxels.Add(new Vector3i(0, y, 0));
            voxels.Add(new Vector3i(1, y, 0));
            voxels.Add(new Vector3i(0, y, 1));
            voxels.Add(new Vector3i(1, y, 1));
        }

        voxels.Add(new Vector3i(0, 3, 0)); // Фитиль

        return voxels;
    }

    public void Update(float dt)
    {
        if (_exploded) return;
        _timer -= dt;
        if (_timer <= 0) Explode();
    }

    private void Explode()
    {
        _exploded = true;
        var pos = _voxelObject.Position;

        Console.WriteLine("BOOM!");

        _objectService.DestroyVoxelObject(_voxelObject);
        ExplosionSystem.CreateExplosion(_worldService, pos, 4.0f, 200.0f);
    }
}