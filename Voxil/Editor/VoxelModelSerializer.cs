// --- Game/Editor/VoxelModelSerializer.cs ---
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class VoxelModelSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented        = true,
        Converters           = { new JsonStringEnumConverter() }
    };

    public static void Save(VoxelObject model, string path)
    {
        var dto = new VoxelModelDto
        {
            Name      = Path.GetFileNameWithoutExtension(path),
            VoxelSize = model.Scale,
            Voxels    = new List<VoxelDto>()
        };

        foreach (var coord in model.VoxelCoordinates)
        {
            model.VoxelMaterials.TryGetValue(coord, out uint mRaw);
            var mat = mRaw == 0 ? model.Material : (MaterialType)mRaw;

            dto.Voxels.Add(new VoxelDto
            {
                X        = coord.X,
                Y        = coord.Y,
                Z        = coord.Z,
                Material = mat.ToString()
            });
        }

        string json = JsonSerializer.Serialize(dto, Options);
        File.WriteAllText(path, json);
        Console.WriteLine($"[Serializer] Saved {dto.Voxels.Count} voxels → {path}");
    }

    public static VoxelObject Load(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"[Serializer] File not found: {path}");
            return null;
        }

        string json = File.ReadAllText(path);
        var dto     = JsonSerializer.Deserialize<VoxelModelDto>(json, Options);

        var coords    = new List<Vector3i>();
        var materials = new Dictionary<Vector3i, uint>();

        foreach (var v in dto.Voxels)
        {
            var pos = new Vector3i(v.X, v.Y, v.Z);
            coords.Add(pos);

            if (Enum.TryParse<MaterialType>(v.Material, out var mat))
                materials[pos] = (uint)mat;
        }

        var model = new VoxelObject(coords, MaterialType.Stone, dto.VoxelSize);

        // Применяем индивидуальные материалы вокселей
        foreach (var kvp in materials)
            model.VoxelMaterials[kvp.Key] = kvp.Value;

        Console.WriteLine($"[Serializer] Loaded '{dto.Name}' ({coords.Count} voxels)");
        return model;
    }

    // DTO классы
    private class VoxelModelDto
    {
        public string        Name      { get; set; }
        public float         VoxelSize { get; set; }
        public List<VoxelDto> Voxels   { get; set; }
    }

    private class VoxelDto
    {
        public int    X        { get; set; }
        public int    Y        { get; set; }
        public int    Z        { get; set; }
        public string Material { get; set; }
    }
}