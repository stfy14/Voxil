// --- START OF FILE DebugStatsWindow.cs ---
using ImGuiNET;
using System.Numerics;

public class DebugStatsWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible 
    { 
        get => _isVisible; 
        set 
        { 
            _isVisible = value; 
            // Оптимизация: собираем метрики только когда открыто окно
            PerformanceMonitor.IsEnabled = _isVisible; 
        } 
    }

    private string _displayText = "";

    public DebugStatsWindow()
    {
        // По умолчанию выключено
        PerformanceMonitor.IsEnabled = false;
    }

    public void UpdateText(string text) => _displayText = text;
    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowBgAlpha(0.5f);

        // Окно можно перетаскивать, оно ресайзится под текст само
        var flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("Performance Stats", ref _isVisible, flags))
        {
            ImGui.TextColored(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), _displayText);
        }
        ImGui.End();

        // Если закрыли на крестик, отключаем сбор
        if (!_isVisible) PerformanceMonitor.IsEnabled = false;
    }
}