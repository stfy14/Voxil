// /World/Meshing/VoxelMeshBuilder.cs - ZERO ALLOCATION VERSION
using OpenTK.Mathematics;
using System;
using System.Buffers; // ВАЖНО: Нужен для ArrayPool
using System.Collections.Generic;

public static class VoxelMeshBuilder
{
    public static void GenerateMesh(
        IDictionary<Vector3i, MaterialType> voxels,
        List<float> vertices,
        List<float> colors,
        List<float> aoValues,
        Func<Vector3i, bool> isVoxelSolid = null)
    {
        if (voxels.Count == 0) return;

        // --- ЭТАП 1: Подготовка (Bounds) ---
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

        // Размеры с паддингом (+1 блок воздуха с каждой стороны = +2 итого)
        int sizeX = (max.X - min.X) + 3;
        int sizeY = (max.Y - min.Y) + 3;
        int sizeZ = (max.Z - min.Z) + 3;

        int volumeLength = sizeX * sizeY * sizeZ;

        // --- ОПТИМИЗАЦИЯ 1: Арендуем массив Volume из пула ---
        // Мы используем int, потому что это быстрее всего для процессора (0 = пусто)
        int[] volume = ArrayPool<int>.Shared.Rent(volumeLength);

        // Обязательно чистим арендованный массив, так как там может быть мусор
        Array.Clear(volume, 0, volumeLength);

        try
        {
            // Заполняем Volume данными
            foreach (var kvp in voxels)
            {
                // +1 для паддинга
                int lx = kvp.Key.X - min.X + 1;
                int ly = kvp.Key.Y - min.Y + 1;
                int lz = kvp.Key.Z - min.Z + 1;

                // Прямой расчет индекса: x + width * (y + height * z)
                volume[lx + sizeX * (ly + sizeY * lz)] = (int)kvp.Value;
            }

            // --- ОПТИМИЗАЦИЯ 2: Арендуем ОДИН массив Mask для всех проходов ---
            // Берем максимальный возможный размер среза
            int maxFaceSize = Math.Max(sizeX * sizeY, Math.Max(sizeX * sizeZ, sizeY * sizeZ));
            int[] mask = ArrayPool<int>.Shared.Rent(maxFaceSize);

            try
            {
                // Размеры для итерации
                int[] dims = { sizeX, sizeY, sizeZ };

                // Проходим по 3 осям
                for (int d = 0; d < 3; d++)
                {
                    int u = (d + 1) % 3;
                    int v = (d + 2) % 3;

                    // Вектора навигации
                    int[] x = { 0, 0, 0 };
                    int[] q = { 0, 0, 0 };
                    q[d] = 1;

                    int uSize = dims[u];
                    int vSize = dims[v];

                    // Чистим маску перед использованием для новой оси (важно!)
                    // Хотя мы и так ее перезаписываем, но Greedy алгоритм требует чистоты при стирании
                    // Но мы стираем внутри алгоритма, так что тут достаточно просто переиспользовать.

                    // Проходим по глубине (d)
                    for (x[d] = 0; x[d] < dims[d] - 1; x[d]++)
                    {
                        // Два направления: Back Face (0) и Front Face (1)
                        for (int faceDir = 0; faceDir < 2; faceDir++)
                        {
                            bool lookPositive = (faceDir == 1);

                            // A. Заполняем маску
                            // Оптимизация: выносим константы
                            int maskIdx = 0;

                            for (int j = 0; j < vSize; j++)
                            {
                                // Предварительный расчет части индекса volume
                                x[v] = j;
                                int volBaseIdx = x[0] + sizeX * (x[1] + sizeY * x[2]);

                                // Шаг для volume по оси U
                                int uStep = (u == 0) ? 1 : (u == 1 ? sizeX : sizeX * sizeY);

                                // Шаг для next блока (q)
                                int qStep = q[0] + sizeX * (q[1] + sizeY * q[2]);

                                for (int i = 0; i < uSize; i++)
                                {
                                    // Вместо пересчета координат x[u]=i каждый раз, используем смещение
                                    // Текущий индекс в volume
                                    int idxCurr = volBaseIdx + i * uStep;
                                    int idxNext = idxCurr + qStep;

                                    int mCurr = volume[idxCurr];
                                    int mNext = volume[idxNext];

                                    bool hasCurrent = mCurr != 0;
                                    bool hasNext = mNext != 0;

                                    int faceMat = 0; // 0 = null/air

                                    if (lookPositive)
                                    {
                                        if (hasCurrent && !hasNext) faceMat = mCurr;
                                    }
                                    else
                                    {
                                        if (!hasCurrent && hasNext) faceMat = mNext;
                                    }

                                    mask[maskIdx++] = faceMat;
                                }
                            }

                            // B. Greedy Meshing
                            maskIdx = 0;
                            for (int j = 0; j < vSize; j++)
                            {
                                for (int i = 0; i < uSize;)
                                {
                                    int material = mask[maskIdx]; // maskIdx == i + j * uSize

                                    if (material != 0)
                                    {
                                        int width = 1;
                                        int height = 1;

                                        // Ширина
                                        while (i + width < uSize && mask[maskIdx + width] == material)
                                        {
                                            width++;
                                        }

                                        // Высота
                                        bool done = false;
                                        while (j + height < vSize)
                                        {
                                            // Базовый индекс строки в маске
                                            int rowBase = maskIdx + height * uSize;
                                            for (int k = 0; k < width; k++)
                                            {
                                                if (mask[rowBase + k] != material)
                                                {
                                                    done = true;
                                                    break;
                                                }
                                            }
                                            if (done) break;
                                            height++;
                                        }

                                        // C. Генерация
                                        // Восстанавливаем мировые координаты
                                        // world = local - 1 + min

                                        // x[d] уже установлен внешним циклом
                                        Vector3i worldPos = new Vector3i();
                                        worldPos[d] = x[d] - 1 + min[d];
                                        worldPos[u] = i - 1 + min[u];
                                        worldPos[v] = j - 1 + min[v];

                                        AddQuad(vertices, colors, aoValues,
                                                worldPos, width, height,
                                                d, u, v,
                                                lookPositive,
                                                (MaterialType)material);

                                        // Очистка маски
                                        for (int l = 0; l < height; l++)
                                        {
                                            int rowBase = maskIdx + l * uSize;
                                            for (int k = 0; k < width; k++)
                                            {
                                                mask[rowBase + k] = 0;
                                            }
                                        }

                                        i += width;
                                        maskIdx += width;
                                    }
                                    else
                                    {
                                        i++;
                                        maskIdx++;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                // Возвращаем маску в пул
                ArrayPool<int>.Shared.Return(mask);
            }
        }
        finally
        {
            // Возвращаем объем в пул
            ArrayPool<int>.Shared.Return(volume);
        }
    }

    private static void AddQuad(
        List<float> vertices, List<float> colors, List<float> aoValues,
        Vector3i start, int width, int height,
        int dAxis, int uAxis, int vAxis,
        bool isPositiveFace,
        MaterialType material)
    {
        var color = MaterialRegistry.GetColor(material);

        var du = new Vector3(0, 0, 0); du[uAxis] = width;
        var dv = new Vector3(0, 0, 0); dv[vAxis] = height;

        Vector3 v0 = new Vector3(start.X, start.Y, start.Z);
        v0[dAxis] += 1.0f;

        Vector3 v1 = v0 + du;
        Vector3 v2 = v0 + du + dv;
        Vector3 v3 = v0 + dv;

        float ao = 1.0f;

        if (isPositiveFace)
        {
            AddVertex(vertices, colors, aoValues, v0, color, ao);
            AddVertex(vertices, colors, aoValues, v1, color, ao);
            AddVertex(vertices, colors, aoValues, v2, color, ao);

            AddVertex(vertices, colors, aoValues, v0, color, ao);
            AddVertex(vertices, colors, aoValues, v2, color, ao);
            AddVertex(vertices, colors, aoValues, v3, color, ao);
        }
        else
        {
            AddVertex(vertices, colors, aoValues, v0, color, ao);
            AddVertex(vertices, colors, aoValues, v3, color, ao);
            AddVertex(vertices, colors, aoValues, v2, color, ao);

            AddVertex(vertices, colors, aoValues, v0, color, ao);
            AddVertex(vertices, colors, aoValues, v2, color, ao);
            AddVertex(vertices, colors, aoValues, v1, color, ao);
        }
    }

    private static void AddVertex(List<float> vertices, List<float> colors, List<float> aoValues, Vector3 pos, (float r, float g, float b) color, float ao)
    {
        vertices.Add(pos.X);
        vertices.Add(pos.Y);
        vertices.Add(pos.Z);
        colors.Add(color.r);
        colors.Add(color.g);
        colors.Add(color.b);
        aoValues.Add(ao);
    }
}