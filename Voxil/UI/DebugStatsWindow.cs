using ImGuiNET;
using System.Numerics;

public class DebugStatsWindow : IUIWindow
{
    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    private string _displayText = "";

    public void UpdateText(string text)
    {
        _displayText = text;
    }

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.35f);

        var flags = ImGuiWindowFlags.NoDecoration | 
                    ImGuiWindowFlags.AlwaysAutoResize | 
                    ImGuiWindowFlags.NoSavedSettings | 
                    ImGuiWindowFlags.NoFocusOnAppearing | 
                    ImGuiWindowFlags.NoNav | 
                    ImGuiWindowFlags.NoInputs;

        if (ImGui.Begin("DebugOverlay", flags))
        {
            // ИЗМЕНЕНО: Цвет текста теперь чисто белый (1.0f, 1.0f, 1.0f, 1.0f)
            ImGui.TextColored(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), _displayText);
            
            ImGui.End();
        }
    }
}