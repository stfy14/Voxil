using System;
using System.Collections.Generic;

/// <summary>
/// Глобальный реестр сервисов. Заменяет передачу WorldManager/PhysicsWorld
/// через конструкторы по всему проекту.
/// Регистрируй через интерфейс, получай через интерфейс.
/// </summary>
public static class ServiceLocator
{
    private static readonly Dictionary<Type, object> _services = new();

    public static void Register<TInterface>(TInterface implementation)
        where TInterface : class
    {
        _services[typeof(TInterface)] = implementation
                                        ?? throw new ArgumentNullException(nameof(implementation));
    }

    public static TInterface Get<TInterface>()
        where TInterface : class
    {
        if (_services.TryGetValue(typeof(TInterface), out var service))
            return (TInterface)service;

        throw new InvalidOperationException(
            $"[ServiceLocator] Сервис '{typeof(TInterface).Name}' не зарегистрирован. " +
            $"Убедись что Register<T>() вызван до Get<T>().");
    }

    public static bool TryGet<TInterface>(out TInterface service)
        where TInterface : class
    {
        if (_services.TryGetValue(typeof(TInterface), out var raw))
        {
            service = (TInterface)raw;
            return true;
        }
        service = null;
        return false;
    }

    public static void Clear()
    {
        _services.Clear();
    }
}