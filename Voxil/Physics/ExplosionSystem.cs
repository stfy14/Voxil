using OpenTK.Mathematics;
using System;

public static class ExplosionSystem
{
    public static void CreateExplosion(WorldManager world, Vector3 center, float radius, float force)
    {
        int r = (int)Math.Ceiling(radius);
        float rSq = radius * radius;
        Vector3i centerVox = new Vector3i((int)center.X, (int)center.Y, (int)center.Z);
        
        // 1. УДАЛЕНИЕ ВОКСЕЛЕЙ
        for (int x = -r; x <= r; x++)
        {
            for (int y = -r; y <= r; y++)
            {
                for (int z = -r; z <= r; z++)
                {
                    if (x*x + y*y + z*z <= rSq)
                    {
                        Vector3i target = centerVox + new Vector3i(x, y, z);
                        var playerFeet = new Vector3i((int)world.GetPlayerPosition().X, (int)world.GetPlayerPosition().Y - 1, (int)world.GetPlayerPosition().Z);
                        var distVec = target - playerFeet;
                        var distSq = distVec.X * distVec.X + distVec.Y * distVec.Y + distVec.Z * distVec.Z;
                        if (distSq < 4) continue;
                        
                        if (world.IsVoxelSolidGlobal(target))
                        {
                            world.RemoveVoxelGlobal(target, updateMesh: false);
                            world.MarkChunkDirty(target);
                        }
                    }
                }
            }
        }
        
        world.UpdateDirtyChunks();

        // 2. ФИЗИЧЕСКИЙ ИМПУЛЬС (С ЗАЩИТОЙ ОТ NaN)
        var objects = world.GetAllVoxelObjects();
        foreach (var obj in objects)
        {
            var offsetVec = obj.Position - center;
            float dist = offsetVec.Length;
            
            if (dist < radius * 3.0f) 
            {
                // === ЗАЩИТА ОТ ДЕЛЕНИЯ НА НОЛЬ (NaN) ===
                if (dist < 0.01f) 
                {
                    // Если центр взрыва идеально совпадает с центром объекта, толкаем его вверх
                    offsetVec = new Vector3(0, 1, 0); 
                    dist = 0.01f;
                }
                // =======================================

                var dir = offsetVec.Normalized();
                float impact = force * (1.0f - (dist / (radius * 3.0f)));
                if (impact < 0) impact = 0;

                var bodyRef = world.PhysicsWorld.Simulation.Bodies.GetBodyReference(obj.BodyHandle);
                bodyRef.ApplyLinearImpulse(dir.ToSystemNumerics() * impact);
                bodyRef.Awake = true;
            }
        }
    }
}