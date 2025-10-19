// /World/Materials/MaterialRegistry.cs
using System.Collections.Generic;

/// <summary>
/// Статический класс-реестр, который определяет и предоставляет доступ к свойствам всех материалов.
/// В будущем эти данные можно будет загружать из файлов (JSON, XML).
/// </summary>
public static class MaterialRegistry
{
    private static readonly Dictionary<MaterialType, MaterialProperties> _definitions;

    static MaterialRegistry()
    {
        _definitions = new Dictionary<MaterialType, MaterialProperties>
        {
            [MaterialType.Air] = new MaterialProperties((0, 0, 0), 0f),
            [MaterialType.Dirt] = new MaterialProperties((0.55f, 0.27f, 0.07f), 1.3f),
            [MaterialType.Stone] = new MaterialProperties((0.5f, 0.5f, 0.5f), 2.5f),
            [MaterialType.Wood] = new MaterialProperties((0.4f, 0.26f, 0.13f), 0.7f)
        };
    }

    public static MaterialProperties Get(MaterialType type)
    {
        return _definitions.TryGetValue(type, out var props)
            ? props
            : _definitions[MaterialType.Air];
    }

    public static (float r, float g, float b) GetColor(MaterialType type)
    {
        return _definitions.TryGetValue(type, out var props)
           ? props.Color
           : (1.0f, 0.0f, 1.0f); // Розовый для отладки
    }
}