// --- Engine/Scene/SceneManager.cs ---
using System;
using System.Collections.Generic;

public class SceneManager
{
    private readonly Dictionary<Type, IScene> _scenes = new();
    private IScene _current;

    public IScene Current => _current;

    // Регистрируем сцену по типу
    public void Register<T>(T scene) where T : IScene
        => _scenes[typeof(T)] = scene;

    // Переключаемся на сцену
    public void SwitchTo<T>() where T : IScene
    {
        if (!_scenes.TryGetValue(typeof(T), out var next))
        {
            Console.WriteLine($"[SceneManager] Scene '{typeof(T).Name}' not registered.");
            return;
        }

        if (_current == next) return;

        _current?.OnExit();
        _current = next;
        _current.OnEnter();

        Console.WriteLine($"[SceneManager] Switched to '{typeof(T).Name}'.");
    }

    public void Update(float deltaTime, InputManager input)
        => _current?.Update(deltaTime, input);

    public void Render()
        => _current?.Render();

    public void OnResize(int width, int height)
        => _current?.OnResize(width, height);
}