// --- START OF FILE UIWindows.cs ---

using ImGuiNET;
using OpenTK.Mathematics;
using System;
using System.Numerics; // Добавлено для Vector2

public class SettingsWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    private readonly WorldManager _worldManager;
    private readonly GpuRaycastingRenderer _renderer;
    
    private int _renderDist;
    private int _shadowSamples;
    private int _genThreads;
    private int _physThreads;
    
    // Переменная для слайдера бюджета (в процентах)
    private int _budgetPercent;

    public SettingsWindow(WorldManager wm, GpuRaycastingRenderer renderer)
    {
        _worldManager = wm;
        _renderer = renderer;
        
        _renderDist = GameSettings.RenderDistance;
        _shadowSamples = GameSettings.SoftShadowSamples;
        _genThreads = GameSettings.GenerationThreads;
        _physThreads = GameSettings.PhysicsThreads;
        // Конвертируем float 0.3f в int 30 для слайдера
        _budgetPercent = (int)(GameSettings.WorldUpdateBudgetPercentage * 100);
    }

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(450, 600), ImGuiCond.FirstUseEver);
        
        if (ImGui.Begin("Game Settings", ref _isVisible))
        {
            // --- Секция Графики ---
            ImGui.Text("Graphics");
            ImGui.Separator();
            // ... (ваш код для Render Distance, теней и т.д. остается здесь) ...
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
            if (ImGui.Button("Apply Render Distance")) { GameSettings.RenderDistance = _renderDist; _renderer.RequestReallocation(); }
            ImGui.Spacing();
            ImGui.Text("Shadows Mode:");
            if (ImGui.RadioButton("None", GameSettings.CurrentShadowMode == ShadowMode.None)) { GameSettings.CurrentShadowMode = ShadowMode.None; _renderer.ReloadShader(); } ImGui.SameLine();
            if (ImGui.RadioButton("Hard", GameSettings.CurrentShadowMode == ShadowMode.Hard)) { GameSettings.CurrentShadowMode = ShadowMode.Hard; _renderer.ReloadShader(); } ImGui.SameLine();
            if (ImGui.RadioButton("Soft", GameSettings.CurrentShadowMode == ShadowMode.Soft)) { GameSettings.CurrentShadowMode = ShadowMode.Soft; _renderer.ReloadShader(); }
            if (GameSettings.CurrentShadowMode == ShadowMode.Soft) { if (ImGui.SliderInt("Soft Samples", ref _shadowSamples, 2, 64)) GameSettings.SoftShadowSamples = _shadowSamples; }
            ImGui.Spacing();
            ImGui.Text("Effects");
            bool ao = GameSettings.EnableAO; if (ImGui.Checkbox("Ambient Occlusion", ref ao)) { GameSettings.EnableAO = ao; _renderer.ReloadShader(); }
            bool water = GameSettings.UseProceduralWater; if (ImGui.Checkbox("Procedural Water (Disable)", ref water)) { GameSettings.UseProceduralWater = water; _renderer.ReloadShader(); }
            bool trans = GameSettings.EnableWaterTransparency; if (ImGui.Checkbox("Water Transparency (Disable)", ref trans)) { GameSettings.EnableWaterTransparency = trans; _renderer.ReloadShader(); }
            bool beam = GameSettings.BeamOptimization; if (ImGui.Checkbox("Beam Optimization (May increase/reduce FPS)", ref beam)) { GameSettings.BeamOptimization = beam; _renderer.ReloadShader(); }
            bool heatmap = GameSettings.ShowDebugHeatmap; if (ImGui.Checkbox("Debug Heatmap (Debug)", ref heatmap)) { GameSettings.ShowDebugHeatmap = heatmap; }

            // --- Секция CPU & Threads ---
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Text("CPU & Threads");
            ImGui.Separator();

            if (ImGui.SliderInt("Generation Threads", ref _genThreads, 1, Environment.ProcessorCount))
            {
                GameSettings.GenerationThreads = _genThreads;
                _worldManager.SetGenerationThreadCount(_genThreads);
            }

            if (ImGui.SliderInt("Physics Threads", ref _physThreads, 1, Environment.ProcessorCount))
            {
                GameSettings.PhysicsThreads = _physThreads;
                _worldManager.PhysicsWorld.SetThreadCount(_physThreads);
            }

            // --- НОВЫЙ СЛАЙДЕР БЮДЖЕТА ---
            if (ImGui.SliderInt("Main Thread Budget (%)", ref _budgetPercent, 5, 100))
            {
                // Конвертируем обратно в float (30 -> 0.3f) и сохраняем
                GameSettings.WorldUpdateBudgetPercentage = _budgetPercent / 100.0f;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Percentage of frame time the main thread can spend loading chunks.\n" +
                               "Higher = faster world loading, but might reduce base FPS.\n" +
                               "Lower = slower world loading, but more stable FPS.");
            }
            ImGui.TextDisabled($"Max available processors: {Environment.ProcessorCount}");
            
            ImGui.Separator();
            if (ImGui.Button("Reset Counters")) PerformanceMonitor.GetDataAndReset(1.0);
        }
        ImGui.End();
    }
}

// Класс MainMenuWindow остается без изменений
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