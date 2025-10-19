// /World/Meshing/VoxelMeshBuilder.cs
using OpenTK.Mathematics;
using System.Collections.Generic;

/// <summary>
/// Специализированный класс, отвечающий за создание меша для набора вокселей.
/// Содержит всю сложную логику по генерации вершин, AO и триангуляции.
/// </summary>
public static class VoxelMeshBuilder
{
    public static void GenerateMesh(ICollection<Vector3i> voxelCoordinates, MaterialType material,
                                    out List<float> vertices, out List<float> colors, out List<float> aoValues)
    {
        vertices = new List<float>();
        colors = new List<float>();
        aoValues = new List<float>();

        if (voxelCoordinates.Count == 0) return;

        var voxelSet = new HashSet<Vector3i>(voxelCoordinates);
        var blockColor = MaterialRegistry.GetColor(material);

        foreach (var coord in voxelCoordinates)
        {
            for (int i = 0; i < 6; i++)
            {
                if (!voxelSet.Contains(coord + FaceDirections[i]))
                {
                    AddFace(vertices, colors, aoValues, coord, (Face)i, blockColor, voxelSet);
                }
            }
        }
    }

    #region Private Mesh Generation Logic
    private enum Face { Top, Bottom, Right, Left, Front, Back }

    private static readonly Vector3i[] FaceDirections = {
        new(0, 1, 0), new(0, -1, 0), new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1)
    };

    private static readonly Vector3[] VoxelCorners = {
        new(-0.5f, -0.5f,  0.5f), new( 0.5f, -0.5f,  0.5f), new( 0.5f,  0.5f,  0.5f), new(-0.5f,  0.5f,  0.5f),
        new(-0.5f, -0.5f, -0.5f), new( 0.5f, -0.5f, -0.5f), new( 0.5f,  0.5f, -0.5f), new(-0.5f,  0.5f, -0.5f)
    };

    private static readonly int[][] FaceCornerIndices = {
        new[] { 3, 2, 6, 7 }, new[] { 0, 4, 5, 1 }, new[] { 1, 5, 6, 2 },
        new[] { 4, 0, 3, 7 }, new[] { 0, 1, 2, 3 }, new[] { 5, 4, 7, 6 }
    };

    private static readonly Vector3i[][] AONeighbors = {
        new Vector3i[] { // Top
            new(-1, 1, 0), new(0, 1, 1), new(-1, 1, 1), new(1, 1, 0), new(0, 1, 1), new(1, 1, 1),
            new(1, 1, 0), new(0, 1, -1), new(1, 1, -1), new(-1, 1, 0), new(0, 1, -1), new(-1, 1, -1)
        },
        new Vector3i[] { // Bottom
            new(-1, -1, 0), new(0, -1, 1), new(-1, -1, 1), new(-1, -1, 0), new(0, -1, -1), new(-1, -1, -1),
            new(1, -1, 0), new(0, -1, -1), new(1, -1, -1), new(1, -1, 0), new(0, -1, 1), new(1, -1, 1)
        },
        new Vector3i[] { // Right
            new(1, -1, 0), new(1, 0, 1), new(1, -1, 1), new(1, -1, 0), new(1, 0, -1), new(1, -1, -1),
            new(1, 1, 0), new(1, 0, -1), new(1, 1, -1), new(1, 1, 0), new(1, 0, 1), new(1, 1, 1)
        },
        new Vector3i[] { // Left
            new(-1, -1, 0), new(-1, 0, -1), new(-1, -1, -1), new(-1, -1, 0), new(-1, 0, 1), new(-1, -1, 1),
            new(-1, 1, 0), new(-1, 0, 1), new(-1, 1, 1), new(-1, 1, 0), new(-1, 0, -1), new(-1, 1, -1)
        },
        new Vector3i[] { // Front
            new(-1, 0, 1), new(0, -1, 1), new(-1, -1, 1), new(1, 0, 1), new(0, -1, 1), new(1, -1, 1),
            new(1, 0, 1), new(0, 1, 1), new(1, 1, 1), new(-1, 0, 1), new(0, 1, 1), new(-1, 1, 1)
        },
        new Vector3i[] { // Back
            new(1, 0, -1), new(0, -1, -1), new(1, -1, -1), new(-1, 0, -1), new(0, -1, -1), new(-1, -1, -1),
            new(-1, 0, -1), new(0, 1, -1), new(-1, 1, -1), new(1, 0, -1), new(0, 1, -1), new(1, 1, -1)
        }
    };

    private static void AddFace(List<float> vertices, List<float> colors, List<float> aoValues,
                               Vector3i localPos, Face face, (float r, float g, float b) color,
                               HashSet<Vector3i> voxelSet)
    {
        int faceIndex = (int)face;
        var cornerIndices = FaceCornerIndices[faceIndex];
        var aoNeighbors = AONeighbors[faceIndex];

        float[] cornerAO = new float[4];
        for (int i = 0; i < 4; i++)
        {
            int offset = i * 3;
            cornerAO[i] = CalculateVertexAO(
                localPos + aoNeighbors[offset], localPos + aoNeighbors[offset + 1], localPos + aoNeighbors[offset + 2], voxelSet);
        }

        if (cornerAO[0] + cornerAO[2] > cornerAO[1] + cornerAO[3])
        {
            AddTriangle(vertices, colors, aoValues, localPos, cornerIndices, cornerAO, color, 0, 1, 3);
            AddTriangle(vertices, colors, aoValues, localPos, cornerIndices, cornerAO, color, 1, 2, 3);
        }
        else
        {
            AddTriangle(vertices, colors, aoValues, localPos, cornerIndices, cornerAO, color, 0, 1, 2);
            AddTriangle(vertices, colors, aoValues, localPos, cornerIndices, cornerAO, color, 0, 2, 3);
        }
    }

    private static void AddTriangle(List<float> vertices, List<float> colors, List<float> aoValues,
                                   Vector3i localPos, int[] cornerIndices, float[] cornerAO,
                                   (float r, float g, float b) color, int idx0, int idx1, int idx2)
    {
        AddVertex(vertices, colors, aoValues, localPos + VoxelCorners[cornerIndices[idx0]], color, cornerAO[idx0]);
        AddVertex(vertices, colors, aoValues, localPos + VoxelCorners[cornerIndices[idx1]], color, cornerAO[idx1]);
        AddVertex(vertices, colors, aoValues, localPos + VoxelCorners[cornerIndices[idx2]], color, cornerAO[idx2]);
    }

    private static float CalculateVertexAO(Vector3i side1, Vector3i side2, Vector3i corner, HashSet<Vector3i> voxelSet)
    {
        bool s1 = voxelSet.Contains(side1);
        bool s2 = voxelSet.Contains(side2);
        bool c = voxelSet.Contains(corner);

        if (s1 && s2) return 0.6f;

        int blocked = (s1 ? 1 : 0) + (s2 ? 1 : 0) + (c ? 1 : 0);
        return 1.0f - (blocked * 0.10f);
    }

    private static void AddVertex(List<float> vertices, List<float> colors, List<float> aoValues,
                                 Vector3 pos, (float r, float g, float b) color, float ao)
    {
        vertices.AddRange(new[] { pos.X, pos.Y, pos.Z });
        colors.AddRange(new[] { color.r, color.g, color.b });
        aoValues.Add(ao);
    }
    #endregion
}