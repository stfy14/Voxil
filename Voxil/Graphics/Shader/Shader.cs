using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public class Shader : IDisposable
{
    public static string GlobalBanksInjection = "";

    public readonly int Handle;
    private readonly Dictionary<string, int> _uniformLocations = new();
    private readonly string _name;
    private bool _disposed;

    // --- 1. Конструктор для ФАЙЛОВ ---
    public Shader(string vertexPath, string fragmentPath, List<string> defines = null)
    {
        _name = $"{vertexPath}+{fragmentPath}";
        string vsSource = LoadSource(vertexPath);
        string fsSource = LoadSource(fragmentPath);

        vsSource = InjectDefines(vsSource, defines);
        fsSource = InjectDefines(fsSource, defines);

        // !!! ИСПРАВЛЕНИЕ: Ищем блочный комментарий, который не удаляется парсером !!!
        fsSource = fsSource.Replace("/*__BANKS_INJECTION__*/", GlobalBanksInjection);

        int vs = CompileShader(ShaderType.VertexShader, vsSource);
        int fs = CompileShader(ShaderType.FragmentShader, fsSource);

        Handle = LinkProgram(vs, fs);
        CacheUniformLocations();
    }

    // --- 2. Конструктор для COMPUTE SHADER ---
    public Shader(string computePath, List<string> defines = null)
    {
        _name = computePath;
        string source = LoadSource(computePath);

        source = InjectDefines(source, defines);

        // !!! ИСПРАВЛЕНИЕ !!!
        source = source.Replace("/*__BANKS_INJECTION__*/", GlobalBanksInjection);

        int cs = CompileShader(ShaderType.ComputeShader, source);
        Handle = LinkProgram(cs);
        CacheUniformLocations();
    }

    // --- 3. Конструктор для СЫРОГО КОДА ---
    public Shader(string vertexSource, string fragmentSource, bool isSourceCode)
    {
        if (!isSourceCode)
            throw new ArgumentException("Use the path-based constructor for files.");

        _name = "Generated/Internal";

        vertexSource = RemoveComments(vertexSource);
        fragmentSource = RemoveComments(fragmentSource);

        int vs = CompileShader(ShaderType.VertexShader, vertexSource);
        int fs = CompileShader(ShaderType.FragmentShader, fragmentSource);

        Handle = LinkProgram(vs, fs);
        CacheUniformLocations();
    }

    // --- Вспомогательные методы ---

    private static string LoadSource(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Shader not found: {path}");
        string source = File.ReadAllText(path);

        // Удаляем комментарии (//), но /* ... */ остаются
        source = RemoveComments(source);

        string dir = Path.GetDirectoryName(path);
        source = ParseIncludes(source, dir);
        return source;
    }

    private static string RemoveComments(string source)
    {
        var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var cleanLine = line;
            // Удаляем только // комментарии
            int commentIndex = cleanLine.IndexOf("//");
            if (commentIndex >= 0)
            {
                cleanLine = cleanLine.Substring(0, commentIndex);
            }
            sb.AppendLine(cleanLine);
        }
        return sb.ToString();
    }

    private static string ParseIncludes(string source, string currentDir)
    {
        var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.Trim().StartsWith("#include"))
            {
                int firstQuote = line.IndexOf('"');
                int lastQuote = line.LastIndexOf('"');
                if (firstQuote > 0 && lastQuote > firstQuote)
                {
                    string includeFile = line.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                    string includePath = Path.Combine(currentDir, includeFile);

                    if (File.Exists(includePath))
                    {
                        string includeSrc = File.ReadAllText(includePath);
                        includeSrc = RemoveComments(includeSrc);
                        includeSrc = ParseIncludes(includeSrc, Path.GetDirectoryName(includePath));
                        sb.AppendLine(includeSrc);
                    }
                    else
                    {
                        Console.WriteLine($"[Shader] Warning: Include not found {includePath}");
                        sb.AppendLine(line);
                    }
                }
                else sb.AppendLine(line);
            }
            else sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static string InjectDefines(string source, List<string> extraDefines)
    {
        string globalDefines = ShaderDefines.GetGlslDefines();
        var sb = new StringBuilder();
        sb.Append(globalDefines);
        if (extraDefines != null) foreach (var def in extraDefines) sb.AppendLine($"#define {def}");
        int versionIndex = source.IndexOf("#version");
        int insertIndex = versionIndex != -1 ? source.IndexOf('\n', versionIndex) + 1 : 0;
        return source.Insert(insertIndex, sb.ToString());
    }

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
            Console.WriteLine($"Error compiling {type} ({_name}).\nLog:\n{infoLog}");
            throw new Exception($"Error compiling {type} in '{_name}'");
        }
        return shader;
    }

    private int LinkProgram(params int[] shaders)
    {
        int program = GL.CreateProgram();
        foreach (var s in shaders) GL.AttachShader(program, s);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int success);
        if (success == 0)
        {
            string infoLog = GL.GetProgramInfoLog(program);
            GL.DeleteProgram(program);
            foreach (var s in shaders) GL.DeleteShader(s);
            throw new Exception($"Error linking {_name}:\n{infoLog}");
        }
        foreach (var s in shaders) { GL.DetachShader(program, s); GL.DeleteShader(s); }
        return program;
    }

    private void CacheUniformLocations()
    {
        GL.GetProgram(Handle, GetProgramParameterName.ActiveUniforms, out int count);
        for (int i = 0; i < count; i++)
        {
            string name = GL.GetActiveUniform(Handle, i, out _, out _);
            _uniformLocations[name] = GL.GetUniformLocation(Handle, name);
        }
    }

    public void Use() => GL.UseProgram(Handle);
    public void SetInt(string n, int v) { if (_uniformLocations.TryGetValue(n, out int loc)) GL.Uniform1(loc, v); }
    public void SetFloat(string n, float v) { if (_uniformLocations.TryGetValue(n, out int loc)) GL.Uniform1(loc, v); }
    public void SetVector2(string name, Vector2 data) { if (_uniformLocations.TryGetValue(name, out int loc)) GL.Uniform2(loc, data); }
    public void SetVector3(string n, Vector3 v) { if (_uniformLocations.TryGetValue(n, out int loc)) GL.Uniform3(loc, v); }
    public void SetMatrix4(string n, Matrix4 v) { if (_uniformLocations.TryGetValue(n, out int loc)) GL.UniformMatrix4(loc, false, ref v); }
    public void SetTexture(string n, int h, TextureUnit u) { if (_uniformLocations.TryGetValue(n, out int loc)) { GL.ActiveTexture(u); GL.BindTexture(TextureTarget.Texture2D, h); GL.Uniform1(loc, (int)u - 33984); } }
    public void Dispose() { if (!_disposed) { GL.DeleteProgram(Handle); _disposed = true; } }
}