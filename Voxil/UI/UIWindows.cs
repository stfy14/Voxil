using ImGuiNET;
using OpenTK.Mathematics;
using System;

public class SettingsWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    private readonly WorldManager _worldManager;
    private readonly GpuRaycastingRenderer _renderer;
    private int _renderDist;
    private int _shadowSamples;

    public SettingsWindow(WorldManager wm, GpuRaycastingRenderer renderer)
    {
        _worldManager = wm;
        _renderer = renderer;
        _renderDist = GameSettings.RenderDistance;
        _shadowSamples = GameSettings.SoftShadowSamples;
    }

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(400, 500), ImGuiCond.FirstUseEver);
        
        // Исправление: сохраняем результат Begin
        bool isExpanded = ImGui.Begin("Game Settings", ref _isVisible);

        if (isExpanded)
        {
            ImGui.Text("Graphics");
            ImGui.Separator();

            long totalVramBytes = (long)_renderer.TotalVramMb * 1024 * 1024;
            long safeBudget = Math.Max(0, totalVramBytes - (2500L * 1024 * 1024));
            long futureBytes = _renderer.CalculateMemoryBytesForDistance(_renderDist);
            long currentBytes = _renderer.CurrentAllocatedBytes;
            bool danger = futureBytes > safeBudget;
            
            if (danger) ImGui.PushStyleColor(ImGuiCol.SliderGrab, new System.Numerics.Vector4(1, 0, 0, 1));
            ImGui.SliderInt("Render Distance", ref _renderDist, 4, 128);
            if (danger) ImGui.PopStyleColor();

            float futureMb = futureBytes / (1024f * 1024f);
            float currentMb = currentBytes / (1024f * 1024f);
            float budgetMb = safeBudget / (1024f * 1024f);

            ImGui.TextDisabled($"Allocated Now: {currentMb:F0} MB");
            if (danger) ImGui.TextColored(new System.Numerics.Vector4(1, 0.3f, 0.3f, 1), $"Request: {futureMb:F0} MB (Limit: {budgetMb:F0})");
            else ImGui.Text($"Request: {futureMb:F0} MB / {budgetMb:F0} MB");

            if (ImGui.Button("Apply Render Distance"))
            {
                GameSettings.RenderDistance = _renderDist;
                _renderer.RequestReallocation();
            }

            ImGui.Spacing();
            ImGui.Text("Shadows Mode:");
            if (ImGui.RadioButton("None", GameSettings.CurrentShadowMode == ShadowMode.None)) 
            { GameSettings.CurrentShadowMode = ShadowMode.None; _renderer.ReloadShader(); }
            ImGui.SameLine();
            if (ImGui.RadioButton("Hard", GameSettings.CurrentShadowMode == ShadowMode.Hard)) 
            { GameSettings.CurrentShadowMode = ShadowMode.Hard; _renderer.ReloadShader(); }
            ImGui.SameLine();
            if (ImGui.RadioButton("Soft", GameSettings.CurrentShadowMode == ShadowMode.Soft)) 
            { GameSettings.CurrentShadowMode = ShadowMode.Soft; _renderer.ReloadShader(); }

            if (GameSettings.CurrentShadowMode == ShadowMode.Soft)
            {
                if (ImGui.SliderInt("Soft Samples", ref _shadowSamples, 2, 64))
                    GameSettings.SoftShadowSamples = _shadowSamples;
            }

            ImGui.Spacing();
            ImGui.Text("Effects");
            bool ao = GameSettings.EnableAO;
            if (ImGui.Checkbox("Ambient Occlusion", ref ao)) { GameSettings.EnableAO = ao; _renderer.ReloadShader(); }
            
            bool water = GameSettings.UseProceduralWater;
            if (ImGui.Checkbox("Procedural Water", ref water)) { GameSettings.UseProceduralWater = water; _renderer.ReloadShader(); }
            
            bool trans = GameSettings.EnableWaterTransparency;
            if (ImGui.Checkbox("Water Transparency", ref trans)) { GameSettings.EnableWaterTransparency = trans; _renderer.ReloadShader(); }
            
            bool beam = GameSettings.BeamOptimization;
            if (ImGui.Checkbox("Beam Optimization", ref beam)) { GameSettings.BeamOptimization = beam; _renderer.ReloadShader(); }

            bool heatmap = GameSettings.ShowDebugHeatmap;
            if (ImGui.Checkbox("Debug Heatmap (Cost)", ref heatmap)) { GameSettings.ShowDebugHeatmap = heatmap; }

            ImGui.Separator();
            if (ImGui.Button("Reset Counters")) PerformanceMonitor.GetDataAndReset(1.0);
        }
        ImGui.End(); // Вызов End всегда снаружи if
    }
}

public class MainMenuWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }
    private readonly SettingsWindow _settings;
    private readonly Game _game;

    public MainMenuWindow(Game game, SettingsWindow settings)
    {
        _game = game;
        _settings = settings;
    }
    public void Toggle() => IsVisible = !IsVisible;
    public void Draw()
    {
        if (!IsVisible) return;
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(io.DisplaySize.X * 0.5f, io.DisplaySize.Y * 0.5f), ImGuiCond.Always, new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(300, 0), new System.Numerics.Vector2(300, 1000));
        var flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize;

        if (ImGui.Begin("Main Menu", flags))
        {
            float width = ImGui.GetContentRegionAvail().X;
            if (ImGui.Button("Resume", new System.Numerics.Vector2(width, 30))) Toggle();
            ImGui.Spacing(); 
            if (ImGui.Button("Settings", new System.Numerics.Vector2(width, 30))) _settings.Toggle(); 
            ImGui.Spacing();
            if (ImGui.Button("Exit", new System.Numerics.Vector2(width, 30))) _game.Close();
            ImGui.End();
        }
    }
}