// --- Game/UI/Windows/MainToolbarWindow.cs ---
using ImGuiNET;
using System.Collections.Generic;

public class MainToolbarWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    private readonly Game _game;
    private readonly List<(string Label, IUIWindow Window)> _menuItems = new();

    public MainToolbarWindow(Game game) => _game = game;

    public void RegisterMenuItem(string label, IUIWindow window)
        => _menuItems.Add((label, window));

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;

        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.MenuItem("Resume Game")) IsVisible = false;

            if (ImGui.BeginMenu("Windows"))
            {
                foreach (var (label, window) in _menuItems)
                {
                    bool vis = window.IsVisible;
                    if (ImGui.MenuItem(label, "", ref vis)) window.IsVisible = vis;
                }
                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("Quit to Desktop")) _game.Close();

            ImGui.EndMainMenuBar();
        }
    }
}