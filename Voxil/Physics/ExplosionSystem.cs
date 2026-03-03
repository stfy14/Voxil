// --- START OF FILE ExplosionSystem.cs ---
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

public static class ExplosionSystem
{
    public static void CreateExplosion(WorldManager world, Vector3 center, float radius, float maxDamage)
    {
        int rays = 16; // 16x16x6 = ~1500 лучей во все стороны
        float stepSize = 0.25f; // Шаг луча (равен размеру вокселя для точности)

        var damagedStatics = new Dictionary<Vector3i, float>();
        var damagedDynamics = new Dictionary<VoxelObject, Dictionary<Vector3i, float>>();

        // 1. Broad-phase: Ищем динамику поблизости (чтобы не проверять всю карту)
        var dynamicsInRange = new List<VoxelObject>();
        var dynInvModels = new Dictionary<VoxelObject, Matrix4>();
        float alpha = world.PhysicsWorld.PhysicsAlpha;

        foreach (var obj in world.GetAllVoxelObjects())
        {
            if ((obj.Position - center).LengthSquared < (radius + 5.0f) * (radius + 5.0f))
            {
                dynamicsInRange.Add(obj);
                // Запоминаем обратные матрицы для быстрого перевода лучей в локальные координаты
                dynInvModels[obj] = obj.GetInterpolatedModelMatrix(alpha).Inverted();
            }
        }

        // 2. Пускаем лучи
        for (int x = 0; x < rays; x++)
        {
            for (int y = 0; y < rays; y++)
            {
                for (int z = 0; z < rays; z++)
                {
                    // Пускаем лучи только по краям куба (чтобы они расходились веером)
                    if (x != 0 && x != rays - 1 && y != 0 && y != rays - 1 && z != 0 && z != rays - 1)
                        continue;

                    // Нормализуем направление луча (-1.0 до 1.0)
                    float dirX = (x / (float)(rays - 1)) * 2f - 1f;
                    float dirY = (y / (float)(rays - 1)) * 2f - 1f;
                    float dirZ = (z / (float)(rays - 1)) * 2f - 1f;
                    Vector3 dir = new Vector3(dirX, dirY, dirZ).Normalized();

                    float currentPower = maxDamage;
                    Vector3 endPoint = center; // Для дебага
                    bool stopped = false;

                    // Движемся по лучу
                    for (float d = 0; d <= radius; d += stepSize)
                    {
                        if (currentPower <= 0) 
                        {
                            stopped = true;
                            break; // Луч полностью поглощен препятствиями
                        }

                        Vector3 p = center + dir * d;
                        endPoint = p;

                        // --- ПРОВЕРКА СТАТИКИ ---
                        Vector3i sPos = new Vector3i((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));
                        MaterialType sMat = world.GetMaterialGlobal(sPos);

                        if (sMat != MaterialType.Air)
                        {
                            float hardness = MaterialRegistry.Get(sMat).Hardness;
                            
                            // Сохраняем МАКСИМАЛЬНЫЙ урон, дошедший до блока (защита от наложения лучей)
                            if (!damagedStatics.ContainsKey(sPos)) damagedStatics[sPos] = 0;
                            damagedStatics[sPos] = Math.Max(damagedStatics[sPos], currentPower);

                            // Блок поглощает часть энергии луча
                            currentPower -= hardness * 0.15f; 
                        }

                        // --- ПРОВЕРКА ДИНАМИКИ ---
                        foreach (var dyn in dynamicsInRange)
                        {
                            // Переводим точку взрыва в локальную систему координат объекта
                            Vector3 localP = (new Vector4(p, 1.0f) * dynInvModels[dyn]).Xyz;
                            Vector3i localVox = new Vector3i((int)Math.Floor(localP.X), (int)Math.Floor(localP.Y), (int)Math.Floor(localP.Z));

                            if (dyn.VoxelCoordinates.Contains(localVox))
                            {
                                dyn.VoxelMaterials.TryGetValue(localVox, out uint mRaw);
                                MaterialType dMat = mRaw == 0 ? dyn.Material : (MaterialType)mRaw;
                                float hardness = MaterialRegistry.Get(dMat).Hardness;

                                if (!damagedDynamics.TryGetValue(dyn, out var voxelDict))
                                {
                                    voxelDict = new Dictionary<Vector3i, float>();
                                    damagedDynamics[dyn] = voxelDict;
                                }

                                if (!voxelDict.ContainsKey(localVox)) voxelDict[localVox] = 0;
                                voxelDict[localVox] = Math.Max(voxelDict[localVox], currentPower);

                                currentPower -= hardness * 0.15f;
                            }
                        }

                        // Падение урона с расстоянием (естественное затухание взрыва)
                        currentPower -= maxDamage * (stepSize / radius);
                    }

                    // ДЕБАГ: Рисуем луч
                    if (GameSettings.ShowExplosionRays)
                    {
                        Vector3 rayColor = stopped ? new Vector3(1.0f, 0.1f, 0.1f) : new Vector3(0.1f, 1.0f, 0.1f);
                        DebugDraw.AddLine(center, endPoint, rayColor, 2.0f); // Луч висит 2 секунды
                    }
                }
            }
        }

        // 3. ПРИМЕНЯЕМ УРОН К СТАТИКЕ
        foreach (var kvp in damagedStatics)
        {
            world.ApplyDamageToStatic(kvp.Key, kvp.Value, out bool destroyed);
            if (destroyed) world.MarkChunkDirty(kvp.Key);
        }
        world.UpdateDirtyChunks(); // Обновляем меши статики одним махом

        // 4. ПРИМЕНЯЕМ УРОН К ДИНАМИКЕ
        var dynNeedsRebuild = new HashSet<VoxelObject>();
        foreach (var dynKvp in damagedDynamics)
        {
            var dyn = dynKvp.Key;
            foreach (var voxKvp in dynKvp.Value)
            {
                bool destroyed = dyn.ApplyDamage(voxKvp.Key, voxKvp.Value);
                if (destroyed) dynNeedsRebuild.Add(dyn); // Запоминаем, что объект пострадал
            }
        }

        // Вызываем раскол только 1 раз для каждого пострадавшего объекта (оптимизация)
        foreach (var dyn in dynNeedsRebuild)
        {
            world.ProcessDynamicObjectSplits(dyn);
        }

        // 5. РАЗЛЕТ ОСКОЛКОВ И ФИЗИЧЕСКИЙ ИМПУЛЬС
        foreach (var obj in world.GetAllVoxelObjects())
        {
            var offsetVec = obj.Position - center;
            float dist = offsetVec.Length;

            if (dist < radius * 3.0f) // Радиус толчка больше радиуса урона (взрывная волна)
            {
                // Защита от деления на ноль и NaN (когда центр взрыва совпадает с центром объекта)
                if (dist < 0.01f) { offsetVec = new Vector3(0, 1, 0); dist = 0.01f; }
                
                var impulseDir = offsetVec.Normalized();
                
                // Сила отталкивания зависит от дистанции
                float impact = maxDamage * 1.5f * (1.0f - (dist / (radius * 3.0f)));
                if (impact < 0) impact = 0;

                if (world.PhysicsWorld.Simulation.Bodies.BodyExists(obj.BodyHandle))
                {
                    var bodyRef = world.PhysicsWorld.Simulation.Bodies.GetBodyReference(obj.BodyHandle);
                    bodyRef.ApplyLinearImpulse(impulseDir.ToSystemNumerics() * impact);
                    bodyRef.Awake = true;
                }
            }
        }

        // ДЕБАГ: Рисуем сферу радиуса взрыва
        if (GameSettings.ShowExplosionRadius)
        {
            DebugDraw.AddSphere(center, radius, new Vector3(1.0f, 0.5f, 0.0f), 2.0f);
        }
    }
}