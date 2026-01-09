using OpenTK.Windowing.Desktop;
using System;
using System.Collections.Generic;

public interface IUIWindow
{
    void Draw();
    bool IsVisible { get; set; }
    void Toggle();
}

public class WindowManager : IDisposable
{
    private readonly ImGuiController _controller;
    private readonly List<IUIWindow> _windows = new List<IUIWindow>();
    
    public bool IsAnyWindowOpen { get; private set; } = false;

    public WindowManager(GameWindow window)
    {
        // ИСПРАВЛЕНИЕ: Используем ClientSize вместо Size
        _controller = new ImGuiController(window.ClientSize.X, window.ClientSize.Y);
    }

    public void AddWindow(IUIWindow window) => _windows.Add(window);

    public void Update(GameWindow window, float deltaTime)
    {
        _controller.Update(window, deltaTime);
        
        IsAnyWindowOpen = false;
        foreach (var w in _windows)
        {
            if (w.IsVisible) IsAnyWindowOpen = true;
        }
    }

    public void Render()
    {
        foreach (var w in _windows)
        {
            if (w.IsVisible) w.Draw();
        }
        _controller.Render();
    }

    public void Resize(int width, int height)
    {
        _controller.WindowResized(width, height);
    }

    public void Dispose()
    {
        _controller.Dispose();
    }
}