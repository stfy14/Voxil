// /World/Meshing/VoxelMeshBuilder.cs - FAST ARRAY VERSION
using OpenTK.Mathematics;
using System;
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

        // --- ЭТАП 1: Подготовка данных (Оптимизация) ---

        // 1. Находим границы
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

        // Размеры объема с учетом паддинга (1 блок воздуха вокруг)
        // +1 для инклюзивности max, +2 для паддинга с двух сторон
        int sizeX = (max.X - min.X) + 3;
        int sizeY = (max.Y - min.Y) + 3;
        int sizeZ = (max.Z - min.Z) + 3;

        // Создаем быстрый плоский массив (симуляция 3D)
        // Это намного быстрее словаря
        var volume = new MaterialType?[sizeX * sizeY * sizeZ];

        // Заполняем массив данными из словаря
        // Индекс [1, 1, 1] в массиве соответствует min в мире
        foreach (var kvp in voxels)
        {
            int lx = kvp.Key.X - min.X + 1;
            int ly = kvp.Key.Y - min.Y + 1;
            int lz = kvp.Key.Z - min.Z + 1;

            volume[lx + sizeX * (ly + sizeY * lz)] = kvp.Value;
        }

        // Вспомогательная функция для чтения из быстрого массива
        // Возвращает null, если пусто
        MaterialType? GetMat(int x, int y, int z)
        {
            return volume[x + sizeX * (y + sizeY * z)];
        }

        // --- ЭТАП 2: Алгоритм Greedy Meshing (без словаря) ---

        // Размеры массива по осям для цикла
        int[] dims = { sizeX, sizeY, sizeZ };

        // Проходим по 3 осям (Dimensions)
        for (int d = 0; d < 3; d++)
        {
            int u = (d + 1) % 3;
            int v = (d + 2) % 3;

            // Вектора для навигации внутри массива
            int[] x = { 0, 0, 0 };
            int[] q = { 0, 0, 0 };
            q[d] = 1;

            int uSize = dims[u];
            int vSize = dims[v];
            var mask = new MaterialType?[uSize * vSize];

            // Проходим по главной оси (Depth) внутри границ массива
            // (от 0 до dims[d]-2, так как мы сравниваем x и x+1)
            for (x[d] = 0; x[d] < dims[d] - 1; x[d]++)
            {
                // Два прохода: Back Face и Front Face
                for (int faceDir = 0; faceDir < 2; faceDir++)
                {
                    bool lookPositive = (faceDir == 1);

                    // A. Заполняем маску (БЕЗ TryGetValue, просто массив)
                    for (int j = 0; j < vSize; j++)
                    {
                        for (int i = 0; i < uSize; i++)
                        {
                            x[u] = i;
                            x[v] = j;

                            // Читаем напрямую из массива volume
                            // current = x
                            // next = x + q
                            MaterialType? matCurr = GetMat(x[0], x[1], x[2]);
                            MaterialType? matNext = GetMat(x[0] + q[0], x[1] + q[1], x[2] + q[2]);

                            bool hasCurrent = matCurr.HasValue;
                            bool hasNext = matNext.HasValue;

                            MaterialType? faceMat = null;

                            if (lookPositive)
                            {
                                // Front Face: Current есть, Next нет
                                if (hasCurrent && !hasNext) faceMat = matCurr;
                            }
                            else
                            {
                                // Back Face: Current нет, Next есть
                                if (!hasCurrent && hasNext) faceMat = matNext;
                            }

                            mask[i + j * uSize] = faceMat;
                        }
                    }

                    // B. Greedy Meshing (тот же самый надежный алгоритм)
                    for (int j = 0; j < vSize; j++)
                    {
                        for (int i = 0; i < uSize;)
                        {
                            int index = i + j * uSize;
                            if (mask[index] != null)
                            {
                                var material = mask[index].Value;
                                int width = 1;
                                int height = 1;

                                // Ширина
                                while (i + width < uSize && mask[index + width] == material)
                                {
                                    width++;
                                }

                                // Высота
                                bool done = false;
                                while (j + height < vSize)
                                {
                                    for (int k = 0; k < width; k++)
                                    {
                                        if (mask[(i + k) + (j + height) * uSize] != material)
                                        {
                                            done = true;
                                            break;
                                        }
                                    }
                                    if (done) break;
                                    height++;
                                }

                                // C. Генерация Квада (Конвертация обратно в мировые координаты)
                                // Наши x[] сейчас в локальных координатах массива (с учетом паддинга +1)
                                // Нужно вернуть их в мировые: world = local - 1 + min

                                Vector3i worldPos = new Vector3i();
                                worldPos[d] = x[d] - 1 + min[d];
                                worldPos[u] = i - 1 + min[u]; // i соответствует x[u]
                                worldPos[v] = j - 1 + min[v]; // j соответствует x[v]

                                AddQuad(vertices, colors, aoValues,
                                        worldPos,
                                        width, height,
                                        d, u, v,
                                        lookPositive,
                                        material);

                                // Очистка
                                for (int l = 0; l < height; l++)
                                {
                                    for (int k = 0; k < width; k++)
                                    {
                                        mask[(i + k) + (j + l) * uSize] = null;
                                    }
                                }

                                i += width;
                            }
                            else
                            {
                                i++;
                            }
                        }
                    }
                }
            }
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

        // Сдвиг плоскости на границу вокселя
        v0[dAxis] += 1.0f;

        Vector3 v1 = v0 + du;
        Vector3 v2 = v0 + du + dv;
        Vector3 v3 = v0 + dv;

        // AO пока 1.0
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