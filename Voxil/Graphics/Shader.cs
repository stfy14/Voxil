// /Graphics/Shader.cs
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.IO;
using System.Collections.Generic;

public class Shader : IDisposable
{
    public readonly int Handle;
    private readonly Dictionary<string, int> _uniformLocations = new();
    private bool _disposed;

    public Shader(string vertexPath, string fragmentPath)
    {
        if (!File.Exists(vertexPath))
            throw new FileNotFoundException($"Вершинный шейдер не найден: {vertexPath}");

        if (!File.Exists(fragmentPath))
            throw new FileNotFoundException($"Фрагментный шейдер не найден: {fragmentPath}");

        string vertexSource = File.ReadAllText(vertexPath);
        string fragmentSource = File.ReadAllText(fragmentPath);

        int vertexShader = CompileShader(ShaderType.VertexShader, vertexSource, vertexPath);
        int fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource, fragmentPath);

        Handle = LinkProgram(vertexShader, fragmentShader);

        GL.DetachShader(Handle, vertexShader);
        GL.DetachShader(Handle, fragmentShader);
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);

        CacheUniformLocations();
        Console.WriteLine($"[Shader] Шейдерная программа создана успешно (ID: {Handle})");
    }

    private int CompileShader(ShaderType type, string source, string path)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);
        if (success == 0)
        {
            string infoLog = GL.GetShaderInfoLog(shader);
            string shaderTypeName = type == ShaderType.VertexShader ? "вершинного" : "фрагментного";
            throw new Exception($"Ошибка компиляции {shaderTypeName} шейдера ({path}):\n{infoLog}");
        }
        return shader;
    }

    private int LinkProgram(int vertexShader, int fragmentShader)
    {
        int program = GL.CreateProgram();
        GL.AttachShader(program, vertexShader);
        GL.AttachShader(program, fragmentShader);
        GL.LinkProgram(program);

        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int success);
        if (success == 0)
        {
            string infoLog = GL.GetProgramInfoLog(program);
            throw new Exception($"Ошибка линковки шейдерной программы:\n{infoLog}");
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
        Console.WriteLine($"[Shader] Кешировано {uniformCount} uniform-переменных");
    }

    public void Use()
    {
        GL.UseProgram(Handle);
    }

    private int GetUniformLocation(string name)
    {
        if (_uniformLocations.TryGetValue(name, out int location))
        {
            return location;
        }

        location = GL.GetUniformLocation(Handle, name);
        if (location == -1)
        {
            Console.WriteLine($"[Shader] Предупреждение: uniform '{name}' не найден");
        }
        _uniformLocations[name] = location;
        return location;
    }

    public void SetMatrix4(string name, Matrix4 matrix)
    {
        int location = GetUniformLocation(name);
        if (location != -1) GL.UniformMatrix4(location, false, ref matrix);
    }

    public void SetVector3(string name, Vector3 vector)
    {
        int location = GetUniformLocation(name);
        if (location != -1) GL.Uniform3(location, vector);
    }

    public void SetFloat(string name, float value)
    {
        int location = GetUniformLocation(name);
        if (location != -1) GL.Uniform1(location, value);
    }

    public void SetInt(string name, int value)
    {
        int location = GetUniformLocation(name);
        if (location != -1) GL.Uniform1(location, value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        GL.DeleteProgram(Handle);
        _disposed = true;
        GC.SuppressFinalize(this);
        Console.WriteLine($"[Shader] Программа {Handle} удалена");
    }

    ~Shader()
    {
        if (!_disposed)
        {
            Console.WriteLine($"[ПРЕДУПРЕЖДЕНИЕ] Shader {Handle} не был корректно освобождён!");
        }
    }
}