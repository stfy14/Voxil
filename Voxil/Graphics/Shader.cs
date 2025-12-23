using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.IO;

public class Shader : IDisposable
{
    public readonly int Handle;
    private readonly Dictionary<string, int> _uniformLocations = new();
    private readonly string _name; // Имя для дебага
    private bool _disposed;

    // --- 1. Конструктор для Vertex + Fragment (ПУТИ К ФАЙЛАМ) ---
    public Shader(string vertexPath, string fragmentPath)
        : this(LoadSource(vertexPath), LoadSource(fragmentPath), $"{vertexPath}+{fragmentPath}")
    {
    }

    // --- 2. Конструктор для Vertex + Fragment (ИСХОДНЫЙ КОД) ---
    // isSourceCode - просто флаг для сигнатуры, чтобы отличаться от первого конструктора
    public Shader(string vertexSource, string fragmentSource, bool isSourceCode)
        : this(vertexSource, fragmentSource, "Generated/Internal")
    {
    }

    // --- 3. Конструктор для Compute Shader (ПУТЬ К ФАЙЛУ) ---
    public Shader(string computePath)
    {
        _name = computePath;
        string source = LoadSource(computePath);

        int cs = CompileShader(ShaderType.ComputeShader, source);
        Handle = LinkProgram(cs); // Линкуем один шейдер

        CacheUniformLocations();
    }

    // --- Приватный мастер-конструктор для стандартной пары Vert+Frag ---
    private Shader(string vertSource, string fragSource, string name)
    {
        _name = name;
        int vs = CompileShader(ShaderType.VertexShader, vertSource);
        int fs = CompileShader(ShaderType.FragmentShader, fragSource);

        Handle = LinkProgram(vs, fs); // Линкуем два шейдера

        CacheUniformLocations();
        Console.WriteLine($"[Shader] Compiled: {_name} (ID: {Handle})");
    }

    // --- Вспомогательные методы ---

    private static string LoadSource(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Shader file not found: {path}");
        return File.ReadAllText(path);
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
            // Удаляем шейдер, чтобы не висел в памяти при ошибке
            GL.DeleteShader(shader);
            throw new Exception($"Error compiling {type} in '{_name}':\n{infoLog}");
        }
        return shader;
    }

    // Универсальный линковщик: принимает любое кол-во шейдеров
    private int LinkProgram(params int[] shaders)
    {
        int program = GL.CreateProgram();

        // 1. Attach
        foreach (var shader in shaders) GL.AttachShader(program, shader);

        // 2. Link
        GL.LinkProgram(program);

        // 3. Check Errors
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int success);
        if (success == 0)
        {
            string infoLog = GL.GetProgramInfoLog(program);
            GL.DeleteProgram(program);
            // Шейдеры удалим ниже, чтобы не было утечек
            foreach (var shader in shaders) GL.DeleteShader(shader);
            throw new Exception($"Error linking program '{_name}':\n{infoLog}");
        }

        // 4. Detach & Delete (Clean up immediately)
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

    // --- API для установки Uniforms ---

    public void Use() => GL.UseProgram(Handle);

    private int GetLoc(string name)
    {
        if (_uniformLocations.TryGetValue(name, out int loc)) return loc;

        // Пытаемся найти, если не закешировалось (например, массивы структур иногда хитро именуются)
        loc = GL.GetUniformLocation(Handle, name);
        _uniformLocations[name] = loc; // Кэшируем даже -1, чтобы не спамить GL вызовами

        if (loc == -1) Console.WriteLine($"[Shader Warning] Uniform '{name}' missing in '{_name}'");
        return loc;
    }

    public void SetMatrix4(string name, Matrix4 data)
    {
        int loc = GetLoc(name); if (loc != -1) GL.UniformMatrix4(loc, false, ref data);
    }
    public void SetVector3(string name, Vector3 data)
    {
        int loc = GetLoc(name); if (loc != -1) GL.Uniform3(loc, data);
    }
    public void SetVector4(string name, Vector4 data)
    {
        int loc = GetLoc(name); if (loc != -1) GL.Uniform4(loc, data);
    }
    public void SetFloat(string name, float data)
    {
        int loc = GetLoc(name); if (loc != -1) GL.Uniform1(loc, data);
    }
    public void SetInt(string name, int data)
    {
        int loc = GetLoc(name); if (loc != -1) GL.Uniform1(loc, data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        GL.DeleteProgram(Handle);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}