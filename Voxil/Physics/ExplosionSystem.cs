using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

public static class ExplosionSystem
{
    public static void CreateExplosion(WorldManager world, Vector3 center, float radius, float force)
    {
        int r = (int)Math.Ceiling(radius);
        float rSq = radius * radius;
        
        // Центр в координатах вокселей
        Vector3i centerVox = new Vector3i((int)center.X, (int)center.Y, (int)center.Z);
        
        // 1. УДАЛЕНИЕ ВОКСЕЛЕЙ (Teardown style)
        // Проходим по кубу вокруг центра
        for (int x = -r; x <= r; x++)
        {
            for (int y = -r; y <= r; y++)
            {
                for (int z = -r; z <= r; z++)
                {
                    if (x*x + y*y + z*z <= rSq)
                    {
                        Vector3i target = centerVox + new Vector3i(x, y, z);
                        
                        // === ИСПРАВЛЕНИЕ: Добавлены скобки () и ручной LengthSquared ===
                        var playerFeet = new Vector3i((int)world.GetPlayerPosition().X, (int)world.GetPlayerPosition().Y - 1, (int)world.GetPlayerPosition().Z);
                        var distVec = target - playerFeet;
                        var distSq = distVec.X * distVec.X + distVec.Y * distVec.Y + distVec.Z * distVec.Z;
                        if (distSq < 4) // Не ломать блоки в радиусе 2 вокселей от ног
                            continue;
                        
                        if (world.IsVoxelSolidGlobal(target))
                        {
                            world.RemoveVoxelGlobal(target, updateMesh: false);
                            world.MarkChunkDirty(target);
                        }
                    }
                }
            }
        }
        
        // Принудительно обновляем затронутые чанки (надо реализовать в WorldManager групповое обновление)
        world.UpdateDirtyChunks();

        // 2. ФИЗИЧЕСКИЙ ИМПУЛЬС (Раскидываем обломки)
        var objects = world.GetAllVoxelObjects();
        foreach (var obj in objects)
        {
            float dist = (obj.Position - center).Length;
            if (dist < radius * 3.0f) // Взрывная волна летит дальше разрушения
            {
                var dir = (obj.Position - center).Normalized();
                // Сила падает с расстоянием
                float impact = force * (1.0f - (dist / (radius * 3.0f)));
                if (impact < 0) impact = 0;

                // Применяем импульс к телу BepuPhysics
                var bodyRef = world.PhysicsWorld.Simulation.Bodies.GetBodyReference(obj.BodyHandle);
                bodyRef.ApplyLinearImpulse(dir.ToSystemNumerics() * impact);
                bodyRef.Awake = true;
            }
        }
    }
}