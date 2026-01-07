using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public class Shader : IDisposable
{
    public readonly int Handle;
    private readonly Dictionary<string, int> _uniformLocations = new();
    private readonly string _name;
    private bool _disposed;

    // --- 1. Конструктор: Пути к файлам + Defines ---
    public Shader(string vertexPath, string fragmentPath, List<string> defines = null)
    {
        _name = $"{vertexPath}+{fragmentPath}";

        // 1. Загружаем исходники (с обработкой #include и удалением комментариев)
        string vsSource = LoadSource(vertexPath);
        string fsSource = LoadSource(fragmentPath);

        // 2. Внедряем #define
        if (defines != null && defines.Count > 0)
        {
            vsSource = InjectDefines(vsSource, defines);
            fsSource = InjectDefines(fsSource, defines);
        }

        // 3. Компилируем
        int vs = CompileShader(ShaderType.VertexShader, vsSource);
        int fs = CompileShader(ShaderType.FragmentShader, fsSource);

        Handle = LinkProgram(vs, fs);
        CacheUniformLocations();

        Console.WriteLine($"[Shader] Compiled: {_name} (Defines: {defines?.Count ?? 0})");
    }

    // --- 2. Конструктор: Compute Shader ---
    public Shader(string computePath, List<string> defines = null)
    {
        _name = computePath;
        string source = LoadSource(computePath);

        if (defines != null && defines.Count > 0)
            source = InjectDefines(source, defines);

        int cs = CompileShader(ShaderType.ComputeShader, source);
        Handle = LinkProgram(cs);
        CacheUniformLocations();
    }
    
    // --- 4. Конструктор для RAW SOURCE (Сырой код из строки) ---
    // Используется в LineRenderer и других процедурных генераторах
    public Shader(string vertexSource, string fragmentSource, bool isSourceCode)
    {
        if (!isSourceCode) 
            throw new ArgumentException("Use the path-based constructor for files.");

        _name = "Generated/Internal";
        
        // Для сырого кода обычно не нужны Defines и Includes, 
        // так как это простые встроенные шейдеры.
        // Но на всякий случай удалим комментарии.
        vertexSource = RemoveComments(vertexSource);
        fragmentSource = RemoveComments(fragmentSource);

        int vs = CompileShader(ShaderType.VertexShader, vertexSource);
        int fs = CompileShader(ShaderType.FragmentShader, fragmentSource);

        Handle = LinkProgram(vs, fs);
        CacheUniformLocations();
    }

    // --- INTERNAL LOGIC ---

    private static string LoadSource(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Shader file not found: {path}");

        string source = File.ReadAllText(path);
        
        // СНАЧАЛА удаляем комментарии, чтобы парсер инклюдов не триггерился на закомментированный код
        source = RemoveComments(source);

        string dir = Path.GetDirectoryName(path);
        return ParseIncludes(source, dir);
    }

    private static string RemoveComments(string source)
    {
        var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            // Упрощенное удаление однострочных комментариев //
            // (Для блочных /* */ нужен более сложный парсер, но обычно хватает этого)
            int commentIndex = line.IndexOf("//");
            if (commentIndex >= 0)
                sb.AppendLine(line.Substring(0, commentIndex));
            else
                sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static string ParseIncludes(string source, string currentDir)
    {
        var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            // Проверяем #include только если строка не пустая (комментарии уже удалены)
            if (line.Trim().StartsWith("#include"))
            {
                int firstQuote = line.IndexOf('"');
                int lastQuote = line.LastIndexOf('"');

                if (firstQuote > 0 && lastQuote > firstQuote)
                {
                    string includeFile = line.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                    string includePath = Path.Combine(currentDir, includeFile);

                    if (!File.Exists(includePath))
                        throw new FileNotFoundException($"Include file not found: {includePath}");

                    // Рекурсивно читаем и чистим вложенный файл
                    string includeSource = File.ReadAllText(includePath);
                    includeSource = RemoveComments(includeSource); // <--- ЧИСТИМ И ВКЛЮЧАЕМЫЙ ФАЙЛ
                    
                    // Рекурсивно парсим инклюды внутри него
                    string processedInclude = ParseIncludes(includeSource, Path.GetDirectoryName(includePath));

                    sb.AppendLine($"// --- BEGIN INCLUDE: {includeFile} ---");
                    sb.AppendLine(processedInclude);
                    sb.AppendLine($"// --- END INCLUDE: {includeFile} ---");
                }
                else
                {
                    // Если синтаксис кривой, оставляем как есть, пусть компилятор GLSL ругается
                    sb.AppendLine(line);
                }
            }
            else
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    private static string InjectDefines(string source, List<string> defines)
    {
        int versionIndex = source.IndexOf("#version");
        int insertIndex = 0;

        if (versionIndex != -1)
        {
            int endOfLine = source.IndexOf('\n', versionIndex);
            insertIndex = endOfLine + 1;
        }

        var sb = new StringBuilder();
        foreach (var def in defines)
        {
            sb.AppendLine($"#define {def}");
        }

        return source.Insert(insertIndex, sb.ToString());
    }

    // --- COMPILE & LINK ---

    private int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);
        if (success == 0)
        {
            string infoLog = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            
            // Вывод части кода для дебага, если ошибка неочевидна
            Console.WriteLine($"--- SHADER ERROR SOURCE ({type}) ---");
            var lines = source.Split('\n');
            for(int i=0; i<Math.Min(lines.Length, 20); i++) Console.WriteLine($"{i+1}: {lines[i]}");
            Console.WriteLine("...");
            
            throw new Exception($"Error compiling {type} in '{_name}':\n{infoLog}");
        }
        return shader;
    }

    private int LinkProgram(params int[] shaders)
    {
        int program = GL.CreateProgram();
        foreach (var shader in shaders) GL.AttachShader(program, shader);
        GL.LinkProgram(program);

        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int success);
        if (success == 0)
        {
            string infoLog = GL.GetProgramInfoLog(program);
            GL.DeleteProgram(program);
            foreach (var shader in shaders) GL.DeleteShader(shader);
            throw new Exception($"Error linking program '{_name}':\n{infoLog}");
        }

        foreach (var shader in shaders)
        {
            GL.DetachShader(program, shader);
            GL.DeleteShader(shader);
        }

        return program;
    }

    private void CacheUniformLocations()
    {
        GL.GetProgram(Handle, GetProgramParameterName.ActiveUniforms, out int uniformCount);
        for (int i = 0; i < uniformCount; i++)
        {
            string name = GL.GetActiveUniform(Handle, i, out _, out _);
            int location = GL.GetUniformLocation(Handle, name);
            _uniformLocations[name] = location;
        }
    }

    // --- PUBLIC API ---

    public void Use() => GL.UseProgram(Handle);

    public void SetTexture(string name, int textureHandle, TextureUnit unit)
    {
        int loc = GetLoc(name);
        if (loc == -1) return;
        GL.ActiveTexture(unit);
        GL.BindTexture(TextureTarget.Texture2D, textureHandle);
        GL.Uniform1(loc, (int)unit - (int)TextureUnit.Texture0);
    }

    private int GetLoc(string name)
    {
        if (_uniformLocations.TryGetValue(name, out int loc)) return loc;
        loc = GL.GetUniformLocation(Handle, name);
        _uniformLocations[name] = loc;
        return loc;
    }

    public void SetMatrix4(string name, Matrix4 data) { int loc = GetLoc(name); if (loc != -1) GL.UniformMatrix4(loc, false, ref data); }
    public void SetVector3(string name, Vector3 data) { int loc = GetLoc(name); if (loc != -1) GL.Uniform3(loc, data); }
    public void SetVector4(string name, Vector4 data) { int loc = GetLoc(name); if (loc != -1) GL.Uniform4(loc, data); }
    public void SetFloat(string name, float data) { int loc = GetLoc(name); if (loc != -1) GL.Uniform1(loc, data); }
    public void SetInt(string name, int data) { int loc = GetLoc(name); if (loc != -1) GL.Uniform1(loc, data); }

    public void Dispose()
    {
        if (_disposed) return;
        GL.DeleteProgram(Handle);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}