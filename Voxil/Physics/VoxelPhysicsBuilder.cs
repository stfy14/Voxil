// /Physics/VoxelPhysicsBuilder.cs
using OpenTK.Mathematics;
using System.Collections.Generic;
using BepuVector3 = System.Numerics.Vector3;

public struct VoxelCollider
{
    public BepuVector3 Position;
    public BepuVector3 HalfSize; // Bepu использует половину размера для Box
}

public static class VoxelPhysicsBuilder
{
    /// <summary>
    /// Генерирует список оптимизированных коробок для физики
    /// </summary>
    public static List<VoxelCollider> GenerateColliders(IDictionary<Vector3i, MaterialType> voxels)
    {
        var colliders = new List<VoxelCollider>();
        if (voxels.Count == 0) return colliders;

        // 1. Быстрый перевод в массив (копия логики из мешера)
        var min = new Vector3i(int.MaxValue);
        var max = new Vector3i(int.MinValue);

        foreach (var v in voxels.Keys)
        {
            if (v.X < min.X) min.X = v.X;
            if (v.Y < min.Y) min.Y = v.Y;
            if (v.Z < min.Z) min.Z = v.Z;
            if (v.X > max.X) max.X = v.X;
            if (v.Y > max.Y) max.Y = v.Y;
            if (v.Z > max.Z) max.Z = v.Z;
        }

        int sizeX = (max.X - min.X) + 3;
        int sizeY = (max.Y - min.Y) + 3;
        int sizeZ = (max.Z - min.Z) + 3;

        // Используем bool массив, нам важно только есть блок или нет
        // (Для статики материал не важен, если трение везде одинаковое)
        var solidMap = new bool[sizeX * sizeY * sizeZ];

        foreach (var kvp in voxels)
        {
            if (MaterialRegistry.IsSolidForPhysics(kvp.Value))
            {
                int lx = kvp.Key.X - min.X + 1;
                int ly = kvp.Key.Y - min.Y + 1;
                int lz = kvp.Key.Z - min.Z + 1;
                solidMap[lx + sizeX * (ly + sizeY * lz)] = true;
            }
        }

        bool IsSolid(int x, int y, int z) => solidMap[x + sizeX * (y + sizeY * z)];

        // 2. Линейное объединение (Simple Greedy по оси X)
        // Мы не делаем полное 2D/3D объединение, так как для физики достаточно 
        // сократить количество тел в 3-4 раза, чтобы стало мгновенно.

        // Проходим по всем блокам
        for (int y = 1; y < sizeY - 1; y++)
        {
            for (int z = 1; z < sizeZ - 1; z++)
            {
                for (int x = 1; x < sizeX - 1; x++)
                {
                    if (IsSolid(x, y, z))
                    {
                        // Проверяем, является ли этот блок "поверхностным"
                        // (если он замурован со всех 6 сторон, коллайдер ему не нужен)
                        if (IsInternal(x, y, z, solidMap, sizeX, sizeY))
                        {
                            continue; // Пропускаем внутренние блоки
                        }

                        // Начинаем "растить" коробку вправо
                        int width = 1;

                        // Пока следующий блок есть И он тоже поверхностный (или примыкает)
                        // Упрощение: просто объединяем подряд идущие твердые блоки.
                        // Bepu нормально переваривает пересечения, главное уменьшить кол-во.
                        while (x + width < sizeX - 1 && IsSolid(x + width, y, z))
                        {
                            // Чтобы не создавать лишнего, объединяем только если следующий тоже не "глубоко внутри".
                            // Но проверка IsInternal дорогая. 
                            // Для скорости просто объединяем линию.
                            width++;
                        }

                        // 3. Создаем коллайдер
                        // Координаты в массиве -> Мировые координаты (локально в чанке)
                        // Центр коробки шириной width находится на x + width/2

                        float sizeX_World = width;
                        float sizeY_World = 1f;
                        float sizeZ_World = 1f;

                        float centerX = (x - 1 + min.X) + width / 2f; // Центр по X
                        float centerY = (y - 1 + min.Y) + 0.5f;       // Центр по Y
                        float centerZ = (z - 1 + min.Z) + 0.5f;       // Центр по Z

                        var collider = new VoxelCollider
                        {
                            Position = new BepuVector3(centerX, centerY, centerZ),
                            HalfSize = new BepuVector3(sizeX_World / 2f, sizeY_World / 2f, sizeZ_World / 2f)
                        };
                        colliders.Add(collider);

                        // Пропускаем объединенные блоки
                        x += width - 1;

                        // В маске стирать не надо, так как мы двигаем индекс цикла 'x'
                    }
                }
            }
        }

        return colliders;
    }

    // Проверка: закрыт ли блок со всех 6 сторон
    private static bool IsInternal(int x, int y, int z, bool[] map, int sx, int sy)
    {
        // Быстрый доступ без проверок границ (так как есть паддинг)
        return map[(x + 1) + sx * (y + sy * z)] &&
               map[(x - 1) + sx * (y + sy * z)] &&
               map[x + sx * (y + 1 + sy * z)] &&
               map[x + sx * (y - 1 + sy * z)] &&
               map[x + sx * (y + sy * (z + 1))] &&
               map[x + sx * (y + sy * (z - 1))];
    }
}