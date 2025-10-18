// VoxelObject.cs
using BepuPhysics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

public class VoxelObject : IDisposable
{
    public List<Vector3i> VoxelCoordinates = new();
    public BodyHandle BodyHandle { get; private set; }
    public Vector3 LocalCenterOfMass { get; private set; }
    public Vector3 Position { get; private set; }
    public Quaternion Rotation { get; private set; }

    private int _vertexArrayObject;
    private int _vertexBufferObject;
    private int _colorBufferObject;
    private int _aoBufferObject;
    private int _vertexCount;
    private bool _isMeshDirty = true;
    private bool _disposed;

    public void Initialize(BodyHandle handle, Vector3 localCenterOfMass)
    {
        this.BodyHandle = handle;
        this.LocalCenterOfMass = localCenterOfMass;
    }

    public void UpdatePose(RigidPose pose)
    {
        this.Position = pose.Position.ToOpenTK();
        var orientation = pose.Orientation;
        this.Rotation = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
    }

    public void Render(Shader shader, Matrix4 view, Matrix4 projection)
    {
        if (_isMeshDirty)
        {
            GenerateMesh();
        }
        if (_vertexCount == 0) return;

        Matrix4 model = Matrix4.CreateTranslation(-LocalCenterOfMass) *
                        Matrix4.CreateFromQuaternion(Rotation) *
                        Matrix4.CreateTranslation(Position);

        shader.Use();
        shader.SetMatrix4("model", model);
        shader.SetMatrix4("view", view);
        shader.SetMatrix4("projection", projection);

        GL.BindVertexArray(_vertexArrayObject);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _vertexCount);
        GL.BindVertexArray(0);
    }

    public void GenerateMesh()
    {
        if (VoxelCoordinates.Count == 0)
        {
            CleanupGpuResources();
            _vertexCount = 0;
            _isMeshDirty = false;
            return;
        }

        var vertices = new List<float>();
        var colors = new List<float>();
        var aoValues = new List<float>();
        var voxelSet = new HashSet<Vector3i>(VoxelCoordinates);

        foreach (var coord in VoxelCoordinates)
        {
            var material = MaterialType.Stone;
            var blockColor = Voxel.GetColor(material);

            for (int i = 0; i < 6; i++)
            {
                if (!voxelSet.Contains(coord + FaceDirections[i]))
                {
                    AddFace(vertices, colors, aoValues, coord, (Face)i, blockColor, voxelSet);
                }
            }
        }

        UploadMeshToGpu(vertices, colors, aoValues);
        _isMeshDirty = false;
    }

    #region Mesh Generation

    private enum Face { Top, Bottom, Right, Left, Front, Back }

    private static readonly Vector3i[] FaceDirections = {
        new(0, 1, 0), new(0, -1, 0), new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1)
    };

    private static readonly Vector3[] VoxelCorners = {
        new(-0.5f, -0.5f,  0.5f), // 0 Front-Bottom-Left
        new( 0.5f, -0.5f,  0.5f), // 1 Front-Bottom-Right
        new( 0.5f,  0.5f,  0.5f), // 2 Front-Top-Right
        new(-0.5f,  0.5f,  0.5f), // 3 Front-Top-Left
        new(-0.5f, -0.5f, -0.5f), // 4 Back-Bottom-Left
        new( 0.5f, -0.5f, -0.5f), // 5 Back-Bottom-Right
        new( 0.5f,  0.5f, -0.5f), // 6 Back-Top-Right
        new(-0.5f,  0.5f, -0.5f)  // 7 Back-Top-Left
    };

    // Индексы углов для квадов (4 вершины на грань в порядке для правильной триангуляции)
    // Порядок вершин обеспечивает CCW обход при взгляде на грань снаружи
    private static readonly int[][] FaceCornerIndices = {
        new[] { 3, 2, 6, 7 }, // Top: смотрим сверху, CCW = 3->2->6->7
        new[] { 0, 4, 5, 1 }, // Bottom: смотрим снизу, CCW = 0->4->5->1
        new[] { 1, 5, 6, 2 }, // Right: смотрим справа, CCW = 1->5->6->2
        new[] { 4, 0, 3, 7 }, // Left: смотрим слева, CCW = 4->0->3->7
        new[] { 0, 1, 2, 3 }, // Front: смотрим спереди, CCW = 0->1->2->3
        new[] { 5, 4, 7, 6 }  // Back: смотрим сзади, CCW = 5->4->7->6
    };

    // Смещения для проверки AO для каждой из 4-х вершин на грани
    // Порядок соответствует FaceCornerIndices: для каждой вершины - side1, side2, corner
    private static readonly Vector3i[][] AONeighbors = {
        new Vector3i[] { // Top (3, 2, 6, 7)
            new(-1, 1, 0), new(0, 1, 1), new(-1, 1, 1),     // Вершина 3
            new(1, 1, 0), new(0, 1, 1), new(1, 1, 1),       // Вершина 2
            new(1, 1, 0), new(0, 1, -1), new(1, 1, -1),     // Вершина 6
            new(-1, 1, 0), new(0, 1, -1), new(-1, 1, -1)    // Вершина 7
        },
        new Vector3i[] { // Bottom (0, 4, 5, 1)
            new(-1, -1, 0), new(0, -1, 1), new(-1, -1, 1),  // Вершина 0
            new(-1, -1, 0), new(0, -1, -1), new(-1, -1, -1),// Вершина 4
            new(1, -1, 0), new(0, -1, -1), new(1, -1, -1),  // Вершина 5
            new(1, -1, 0), new(0, -1, 1), new(1, -1, 1)     // Вершина 1
        },
        new Vector3i[] { // Right (1, 5, 6, 2)
            new(1, -1, 0), new(1, 0, 1), new(1, -1, 1),     // Вершина 1
            new(1, -1, 0), new(1, 0, -1), new(1, -1, -1),   // Вершина 5
            new(1, 1, 0), new(1, 0, -1), new(1, 1, -1),     // Вершина 6
            new(1, 1, 0), new(1, 0, 1), new(1, 1, 1)        // Вершина 2
        },
        new Vector3i[] { // Left (4, 0, 3, 7)
            new(-1, -1, 0), new(-1, 0, -1), new(-1, -1, -1),// Вершина 4
            new(-1, -1, 0), new(-1, 0, 1), new(-1, -1, 1),  // Вершина 0
            new(-1, 1, 0), new(-1, 0, 1), new(-1, 1, 1),    // Вершина 3
            new(-1, 1, 0), new(-1, 0, -1), new(-1, 1, -1)   // Вершина 7
        },
        new Vector3i[] { // Front (0, 1, 2, 3)
            new(-1, 0, 1), new(0, -1, 1), new(-1, -1, 1),   // Вершина 0
            new(1, 0, 1), new(0, -1, 1), new(1, -1, 1),     // Вершина 1
            new(1, 0, 1), new(0, 1, 1), new(1, 1, 1),       // Вершина 2
            new(-1, 0, 1), new(0, 1, 1), new(-1, 1, 1)      // Вершина 3
        },
        new Vector3i[] { // Back (5, 4, 7, 6)
            new(1, 0, -1), new(0, -1, -1), new(1, -1, -1),  // Вершина 5
            new(-1, 0, -1), new(0, -1, -1), new(-1, -1, -1),// Вершина 4
            new(-1, 0, -1), new(0, 1, -1), new(-1, 1, -1),  // Вершина 7
            new(1, 0, -1), new(0, 1, -1), new(1, 1, -1)     // Вершина 6
        }
    };

    private void AddFace(List<float> vertices, List<float> colors, List<float> aoValues,
                        Vector3i localPos, Face face, (float r, float g, float b) color,
                        HashSet<Vector3i> voxelSet)
    {
        int faceIndex = (int)face;
        var cornerIndices = FaceCornerIndices[faceIndex];
        var aoNeighbors = AONeighbors[faceIndex];

        // Вычисляем AO для всех 4-х углов грани
        float[] cornerAO = new float[4];
        for (int i = 0; i < 4; i++)
        {
            int offset = i * 3;
            cornerAO[i] = CalculateVertexAO(
                localPos + aoNeighbors[offset],
                localPos + aoNeighbors[offset + 1],
                localPos + aoNeighbors[offset + 2],
                voxelSet
            );
        }

        // Выбираем диагональ с МЕНЬШЕЙ суммой AO (темнее) для более естественного вида
        float diag02 = cornerAO[0] + cornerAO[2];
        float diag13 = cornerAO[1] + cornerAO[3];

        bool useDiag13 = diag13 < diag02;

        if (useDiag13)
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

    private void AddTriangle(List<float> vertices, List<float> colors, List<float> aoValues,
                            Vector3i localPos, int[] cornerIndices, float[] cornerAO,
                            (float r, float g, float b) color, int idx0, int idx1, int idx2)
    {
        AddVertex(vertices, colors, aoValues, localPos + VoxelCorners[cornerIndices[idx0]], color, cornerAO[idx0]);
        AddVertex(vertices, colors, aoValues, localPos + VoxelCorners[cornerIndices[idx1]], color, cornerAO[idx1]);
        AddVertex(vertices, colors, aoValues, localPos + VoxelCorners[cornerIndices[idx2]], color, cornerAO[idx2]);
    }

    private float CalculateVertexAO(Vector3i side1, Vector3i side2, Vector3i corner, HashSet<Vector3i> voxelSet)
    {
        bool s1 = voxelSet.Contains(side1);
        bool s2 = voxelSet.Contains(side2);
        bool c = voxelSet.Contains(corner);

        // Минимальный контраст для устранения артефактов
        if (s1 && s2)
        {
            return 0.6f;  // Максимальное затемнение - не слишком темное
        }

        // Линейное затемнение с маленьким шагом
        int blocked = (s1 ? 1 : 0) + (s2 ? 1 : 0) + (c ? 1 : 0);
        return 1.0f - (blocked * 0.10f);  // Шаг 0.13: 1.0 → 0.87 → 0.74 → 0.61 (≈0.6)
    }

    private void AddVertex(List<float> vertices, List<float> colors, List<float> aoValues,
                          Vector3 pos, (float r, float g, float b) color, float ao)
    {
        vertices.AddRange(new[] { pos.X, pos.Y, pos.Z });
        colors.AddRange(new[] { color.r, color.g, color.b });
        aoValues.Add(ao);
    }

    private void UploadMeshToGpu(List<float> vertices, List<float> colors, List<float> aoValues)
    {
        CleanupGpuResources();
        _vertexCount = vertices.Count / 3;
        if (_vertexCount == 0) return;

        _vertexArrayObject = GL.GenVertexArray();
        GL.BindVertexArray(_vertexArrayObject);

        _vertexBufferObject = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), vertices.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 0, 0);
        GL.EnableVertexAttribArray(0);

        _colorBufferObject = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _colorBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, colors.Count * sizeof(float), colors.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 0, 0);
        GL.EnableVertexAttribArray(1);

        _aoBufferObject = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _aoBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, aoValues.Count * sizeof(float), aoValues.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, 0, 0);
        GL.EnableVertexAttribArray(2);

        GL.BindVertexArray(0);
    }

    private void CleanupGpuResources()
    {
        if (_vertexArrayObject != 0) GL.DeleteVertexArray(_vertexArrayObject);
        if (_vertexBufferObject != 0) GL.DeleteBuffer(_vertexBufferObject);
        if (_colorBufferObject != 0) GL.DeleteBuffer(_colorBufferObject);
        if (_aoBufferObject != 0) GL.DeleteBuffer(_aoBufferObject);
        _vertexArrayObject = _vertexBufferObject = _colorBufferObject = _aoBufferObject = 0;
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        CleanupGpuResources();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}