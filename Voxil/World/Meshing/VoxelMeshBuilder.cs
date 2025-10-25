// /World/Meshing/VoxelMeshBuilder.cs - REFACTORED
using OpenTK.Mathematics;
using System.Collections.Generic;

/// <summary>
/// Специализированный класс для создания мешей с правильным AO и culling'ом граней
/// </summary>
public static class VoxelMeshBuilder
{
    /// <summary>
    /// Генерирует меш для набора вокселей с учётом видимости граней и AO
    /// </summary>
    /// <param name="voxels">Словарь локальных координат и материалов вокселей</param>
    /// <param name="vertices">Выходной список вершин</param>
    /// <param name="colors">Выходной список цветов</param>
    /// <param name="aoValues">Выходной список значений ambient occlusion</param>
    /// <param name="isVoxelSolid">Функция проверки, является ли воксель твёрдым (для AO и culling)</param>
    public static void GenerateMesh(
        IDictionary<Vector3i, MaterialType> voxels,
        // Меняем 'out List<float>' на 'List<float>'
        List<float> vertices,
        List<float> colors,
        List<float> aoValues,
        System.Func<Vector3i, bool> isVoxelSolid = null)
    {
        if (voxels.Count == 0) return;

        // Если функция не передана, используем только локальный словарь
        isVoxelSolid ??= pos => voxels.ContainsKey(pos);

        foreach (var pair in voxels)
        {
            var coord = pair.Key;
            var material = pair.Value;
            var blockColor = MaterialRegistry.GetColor(material);

            // Проходим по всем 6 граням куба
            for (int faceIndex = 0; faceIndex < 6; faceIndex++)
            {
                Vector3i neighborPos = coord + FaceDirections[faceIndex];

                // ОПТИМИЗАЦИЯ: Рендерим грань только если сосед не твёрдый
                if (!isVoxelSolid(neighborPos))
                {
                    AddFace(vertices, colors, aoValues, coord, (Face)faceIndex, blockColor, isVoxelSolid);
                }
            }
        }
    }

    #region Face and Mesh Generation

    private enum Face { Top, Bottom, Right, Left, Front, Back }

    private static readonly Vector3i[] FaceDirections = {
        new(0, 1, 0),   // Top
        new(0, -1, 0),  // Bottom
        new(1, 0, 0),   // Right
        new(-1, 0, 0),  // Left
        new(0, 0, 1),   // Front
        new(0, 0, -1)   // Back
    };

    // Вершины куба (центр в 0,0,0, размер 1x1x1)
    private static readonly Vector3[] VoxelCorners = {
        new(-0.5f, -0.5f,  0.5f), // 0
        new( 0.5f, -0.5f,  0.5f), // 1
        new( 0.5f,  0.5f,  0.5f), // 2
        new(-0.5f,  0.5f,  0.5f), // 3
        new(-0.5f, -0.5f, -0.5f), // 4
        new( 0.5f, -0.5f, -0.5f), // 5
        new( 0.5f,  0.5f, -0.5f), // 6
        new(-0.5f,  0.5f, -0.5f)  // 7
    };

    // Индексы вершин для каждой грани (порядок важен для правильной ориентации нормалей)
    private static readonly int[][] FaceCornerIndices = {
        new[] { 3, 2, 6, 7 }, // Top
        new[] { 0, 4, 5, 1 }, // Bottom
        new[] { 1, 5, 6, 2 }, // Right
        new[] { 4, 0, 3, 7 }, // Left
        new[] { 0, 1, 2, 3 }, // Front
        new[] { 5, 4, 7, 6 }  // Back
    };

    // Соседи для расчёта AO: для каждой грани, для каждой из 4 вершин - 3 соседа (side1, side2, corner)
    private static readonly Vector3i[][] AONeighbors = {
        // Top face (Y+)
        new Vector3i[] {
            // Вершина 0 (3): -X, +Z, corner
            new(-1, 1, 0), new(0, 1, 1), new(-1, 1, 1),
            // Вершина 1 (2): +X, +Z, corner
            new(1, 1, 0), new(0, 1, 1), new(1, 1, 1),
            // Вершина 2 (6): +X, -Z, corner
            new(1, 1, 0), new(0, 1, -1), new(1, 1, -1),
            // Вершина 3 (7): -X, -Z, corner
            new(-1, 1, 0), new(0, 1, -1), new(-1, 1, -1)
        },
        // Bottom face (Y-)
        new Vector3i[] {
            new(-1, -1, 0), new(0, -1, 1), new(-1, -1, 1),
            new(-1, -1, 0), new(0, -1, -1), new(-1, -1, -1),
            new(1, -1, 0), new(0, -1, -1), new(1, -1, -1),
            new(1, -1, 0), new(0, -1, 1), new(1, -1, 1)
        },
        // Right face (X+)
        new Vector3i[] {
            new(1, -1, 0), new(1, 0, 1), new(1, -1, 1),
            new(1, -1, 0), new(1, 0, -1), new(1, -1, -1),
            new(1, 1, 0), new(1, 0, -1), new(1, 1, -1),
            new(1, 1, 0), new(1, 0, 1), new(1, 1, 1)
        },
        // Left face (X-)
        new Vector3i[] {
            new(-1, -1, 0), new(-1, 0, -1), new(-1, -1, -1),
            new(-1, -1, 0), new(-1, 0, 1), new(-1, -1, 1),
            new(-1, 1, 0), new(-1, 0, 1), new(-1, 1, 1),
            new(-1, 1, 0), new(-1, 0, -1), new(-1, 1, -1)
        },
        // Front face (Z+)
        new Vector3i[] {
            new(-1, 0, 1), new(0, -1, 1), new(-1, -1, 1),
            new(1, 0, 1), new(0, -1, 1), new(1, -1, 1),
            new(1, 0, 1), new(0, 1, 1), new(1, 1, 1),
            new(-1, 0, 1), new(0, 1, 1), new(-1, 1, 1)
        },
        // Back face (Z-)
        new Vector3i[] {
            new(1, 0, -1), new(0, -1, -1), new(1, -1, -1),
            new(-1, 0, -1), new(0, -1, -1), new(-1, -1, -1),
            new(-1, 0, -1), new(0, 1, -1), new(-1, 1, -1),
            new(1, 0, -1), new(0, 1, -1), new(1, 1, -1)
        }
    };

    private static void AddFace(
        List<float> vertices,
        List<float> colors,
        List<float> aoValues,
        Vector3i localPos,
        Face face,
        (float r, float g, float b) color,
        System.Func<Vector3i, bool> isVoxelSolid)
    {
        int faceIndex = (int)face;
        var cornerIndices = FaceCornerIndices[faceIndex];
        var aoNeighbors = AONeighbors[faceIndex];

        // Вычисляем AO для каждой из 4 вершин грани
        float[] cornerAO = new float[4];
        for (int i = 0; i < 4; i++)
        {
            int offset = i * 3;
            cornerAO[i] = CalculateVertexAO(
                localPos + aoNeighbors[offset],     // side1
                localPos + aoNeighbors[offset + 1], // side2
                localPos + aoNeighbors[offset + 2], // corner
                isVoxelSolid
            );
        }

        // Добавляем два треугольника для квада (правильная ориентация для предотвращения flickering)
        // ВАЖНО: Порядок вершин должен быть против часовой стрелки (CCW) для front face
        AddTriangle(vertices, colors, aoValues, localPos, cornerIndices, cornerAO, color, 0, 1, 2);
        AddTriangle(vertices, colors, aoValues, localPos, cornerIndices, cornerAO, color, 0, 2, 3);
    }

    private static void AddTriangle(
        List<float> vertices,
        List<float> colors,
        List<float> aoValues,
        Vector3i localPos,
        int[] cornerIndices,
        float[] cornerAO,
        (float r, float g, float b) color,
        int idx0, int idx1, int idx2)
    {
        AddVertex(vertices, colors, aoValues, localPos + VoxelCorners[cornerIndices[idx0]], color, cornerAO[idx0]);
        AddVertex(vertices, colors, aoValues, localPos + VoxelCorners[cornerIndices[idx1]], color, cornerAO[idx1]);
        AddVertex(vertices, colors, aoValues, localPos + VoxelCorners[cornerIndices[idx2]], color, cornerAO[idx2]);
    }

    /// <summary>
    /// Вычисляет ambient occlusion для вершины на основе окружающих блоков
    /// </summary>
    private static float CalculateVertexAO(
        Vector3i side1,
        Vector3i side2,
        Vector3i corner,
        System.Func<Vector3i, bool> isVoxelSolid)
    {
        bool s1 = isVoxelSolid(side1);
        bool s2 = isVoxelSolid(side2);
        bool c = isVoxelSolid(corner);

        // Если оба соседа твёрдые - максимальное затенение
        if (s1 && s2)
            return 0.6f;

        // Иначе затенение зависит от количества заблокированных соседей
        int blocked = (s1 ? 1 : 0) + (s2 ? 1 : 0) + (c ? 1 : 0);

        // Формула AO: чем больше заблокировано - тем темнее
        return 1.0f - (blocked * 0.10f);
    }

    private static void AddVertex(
    List<float> vertices,
    List<float> colors,
    List<float> aoValues,
    Vector3 pos,
    (float r, float g, float b) color,
    float ao)
    {
        // --- OPTIMIZATION ---
        // Было: vertices.AddRange(new[] { pos.X + 0.5f, pos.Y + 0.5f, pos.Z + 0.5f });
        // Стало:
        vertices.Add(pos.X + 0.5f);
        vertices.Add(pos.Y + 0.5f);
        vertices.Add(pos.Z + 0.5f);

        // Было: colors.AddRange(new[] { color.r, color.g, color.b });
        // Стало:
        colors.Add(color.r);
        colors.Add(color.g);
        colors.Add(color.b);

        aoValues.Add(ao);
    }


    #endregion
}