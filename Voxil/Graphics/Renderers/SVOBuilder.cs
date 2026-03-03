// --- SVOBuilder.cs ---
// Строит плоский BFS-сериализованный SVO из набора вокселей.
//
// Формат узла (uvec4, 16 байт, совместимо со std430 GLSL):
//   [0] childMask  : uint — биты 0-7, каждый бит = наличие ребёнка в октанте i
//   [1] childOffset: uint — индекс первого ребёнка в плоском массиве
//                          остальные дети идут подряд (гарантия BFS)
//   [2] material   : uint — ненулевой только в листовых узлах (leaf)
//   [3] padding    : uint — всегда 0
//
// Почему BFS гарантирует непрерывность детей:
//   В BFS все дети одного родителя добавляются в очередь подряд, не перемежаясь
//   с детьми других родителей того же уровня — потому что мы заканчиваем обработку
//   одного уровня полностью до следующего. Это даёт нам адресацию:
//     childIdx(octant) = childOffset + popcount(childMask & ((1 << octant) - 1))

using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Numerics;    // BitOperations.Log2

public static class SVOBuilder
{
    // -------------------------------------------------------------------------
    // Публичный API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Строит SVO и возвращает плоский uint[] массив, готовый к загрузке в SSBO.
    /// </summary>
    /// <param name="voxels">Позиции вокселей в диапазоне [0, gridSize)</param>
    /// <param name="getMaterial">Возвращает MaterialID для позиции (0 = воздух)</param>
    /// <param name="gridSize">Размер сетки, степень двойки (32, 64, 128)</param>
    public static uint[] Build(
        IEnumerable<Vector3i> voxels,
        Func<Vector3i, uint>  getMaterial,
        int                   gridSize)
    {
        if (gridSize <= 0 || (gridSize & (gridSize - 1)) != 0)
            throw new ArgumentException($"gridSize должен быть степенью двойки, получено: {gridSize}");

        int depth = BitOperations.Log2((uint)gridSize); // 128 → 7

        var root = new BuildNode();

        foreach (var pos in voxels)
        {
            uint mat = getMaterial(pos);
            if (mat == 0) continue;

            // Проверяем что позиция в допустимом диапазоне
            if (pos.X < 0 || pos.Y < 0 || pos.Z < 0 ||
                pos.X >= gridSize || pos.Y >= gridSize || pos.Z >= gridSize)
                continue;

            Insert(root, pos, mat, depth);
        }

        return Serialize(root);
    }

    /// <summary>Следующая степень двойки >= value.</summary>
    public static int NextPowerOfTwo(int value)
    {
        if (value <= 1) return 1;
        return 1 << (32 - BitOperations.LeadingZeroCount((uint)(value - 1)));
    }

    // -------------------------------------------------------------------------
    // Внутренние структуры и методы
    // -------------------------------------------------------------------------

    private sealed class BuildNode
    {
        public byte         ChildMask;
        public BuildNode[]  Children = new BuildNode[8];
        public uint         Material; // 0 = внутренний узел или пустой лист
    }

    private static void Insert(BuildNode node, Vector3i pos, uint mat, int depth)
    {
        if (depth == 0)
        {
            node.Material = mat;
            return;
        }

        int half = 1 << (depth - 1);

        // Определяем октант: bit0=X, bit1=Y, bit2=Z
        int oct = (pos.X >= half ? 1 : 0)
                | (pos.Y >= half ? 2 : 0)
                | (pos.Z >= half ? 4 : 0);

        node.Children[oct] ??= new BuildNode();
        node.ChildMask |= (byte)(1 << oct);

        // Смещаем позицию в систему координат октанта
        var childOrigin = new Vector3i(
            (oct & 1) != 0 ? half : 0,
            (oct & 2) != 0 ? half : 0,
            (oct & 4) != 0 ? half : 0);

        Insert(node.Children[oct], pos - childOrigin, mat, depth - 1);
    }

    private static uint[] Serialize(BuildNode root)
    {
        // Двухпроходная BFS-сериализация
        //
        // Проход 1: обходим дерево в ширину, назначаем каждому узлу индекс
        //           в порядке обхода. Дети одного родителя гарантированно
        //           идут подряд и в порядке октантов (0..7).
        //
        // Проход 2: зная все индексы, заполняем плоский массив корректными
        //           childOffset (индекс первого ребёнка).

        var order   = new List<BuildNode>(capacity: 1024);
        var indexOf = new Dictionary<BuildNode, int>(ReferenceEqualityComparer.Instance);
        var queue   = new Queue<BuildNode>();

        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            indexOf[node] = order.Count;
            order.Add(node);

            // Добавляем детей в очередь в порядке октантов — это гарантирует
            // что в плоском массиве они окажутся подряд
            for (int i = 0; i < 8; i++)
                if (node.Children[i] != null)
                    queue.Enqueue(node.Children[i]);
        }

        // Заполняем плоский массив
        var flat = new uint[order.Count * 4];

        for (int i = 0; i < order.Count; i++)
        {
            var node = order[i];

            // Ищем индекс первого существующего ребёнка
            uint firstChildIdx = 0;
            for (int oct = 0; oct < 8; oct++)
            {
                if (node.Children[oct] != null)
                {
                    firstChildIdx = (uint)indexOf[node.Children[oct]];
                    break;
                }
            }

            flat[i * 4 + 0] = node.ChildMask;      // childMask
            flat[i * 4 + 1] = firstChildIdx;         // childOffset
            flat[i * 4 + 2] = node.Material;         // material (0 = внутренний)
            flat[i * 4 + 3] = 0u;                    // padding
        }

        return flat;
    }
}
