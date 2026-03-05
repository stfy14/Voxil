using System;
using System.Collections.Generic;

/// <summary>
/// Глобальная шина событий. Развязывает системы друг от друга —
/// отправитель не знает кто слушает, слушатель не знает кто отправил.
/// </summary>
public static class EventBus
{
    private static readonly Dictionary<Type, List<Delegate>> _handlers = new();

    public static void Subscribe<T>(Action<T> handler)
    {
        var type = typeof(T);
        if (!_handlers.ContainsKey(type))
            _handlers[type] = new List<Delegate>();
        _handlers[type].Add(handler);
    }

    public static void Unsubscribe<T>(Action<T> handler)
    {
        if (_handlers.TryGetValue(typeof(T), out var list))
            list.Remove(handler);
    }

    public static void Publish<T>(T evt)
    {
        if (!_handlers.TryGetValue(typeof(T), out var list)) return;
        // Копия списка на случай если хендлер сам отпишется во время вызова
        foreach (var handler in list.ToArray())
            ((Action<T>)handler).Invoke(evt);
    }

    public static void Clear()
    {
        _handlers.Clear();
    }
}