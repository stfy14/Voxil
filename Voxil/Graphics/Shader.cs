using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.IO;

public class Shader : IDisposable
{
    public readonly int Handle;
    private readonly Dictionary<string, int> _uniformLocations = new();
    private bool _disposed;

    // Новое поле для хранения имени (пути)
    private readonly string _name;

    public Shader(string vertexPath, string fragmentPath)
    {
        // Сохраняем имя для дебага
        _name = $"{vertexPath} + {fragmentPath}";

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
        Console.WriteLine($"[Shader] Шейдер загружен: {_name} (ID: {Handle})");
    }

    // Дополнительный конструктор для кода из строки (как в DebugOverlay/Crosshair)
    public Shader(string vertexSource, string fragmentSource, bool isSourceCode)
    {
        _name = "Generated/Internal"; // Имя для внутренних шейдеров

        int vertexShader = CompileShader(ShaderType.VertexShader, vertexSource, "Internal Vert");
        int fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource, "Internal Frag");

        Handle = LinkProgram(vertexShader, fragmentShader);

        GL.DetachShader(Handle, vertexShader);
        GL.DetachShader(Handle, fragmentShader);
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
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
            throw new Exception($"Ошибка линковки шейдера [{_name}]:\n{infoLog}");
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
            // --- ОБНОВЛЕННЫЙ ВЫВОД ОШИБКИ ---
            // Теперь пишет конкретный файл шейдера
            Console.WriteLine($"[Shader Warning] Uniform '{name}' не найден (или вырезан компилятором) в шейдере: [{_name}]");
        }

        // Кэшируем даже -1, чтобы не спамить в консоль каждый кадр
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

    public void SetVector4(string name, Vector4 vector)
    {
        int location = GetUniformLocation(name);
        if (location != -1) GL.Uniform4(location, vector);
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
    }
}