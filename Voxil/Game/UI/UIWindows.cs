// --- START OF FILE UIWindows.cs ---
using ImGuiNET;
using System;

public class SettingsWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    private readonly WorldManager _worldManager;
    private readonly GpuRaycastingRenderer _renderer;
    
    private int _renderDist;
    private float _currentScale;
    private int _shadowSamples;
    private int _genThreads;
    private int _physThreads;
    private bool _taa;
    private int _budgetPercent;
    private float _lodDist;
    private bool _lodEffectsDisabled;

    public SettingsWindow(WorldManager wm, GpuRaycastingRenderer renderer)
    {
        _worldManager = wm;
        _renderer = renderer;
        _renderDist = GameSettings.RenderDistance;
        _currentScale = GameSettings.RenderScale;
        _shadowSamples = GameSettings.SoftShadowSamples;
        _genThreads = GameSettings.GenerationThreads;
        _physThreads = GameSettings.PhysicsThreads;
        _budgetPercent = (int)(GameSettings.WorldUpdateBudgetPercentage * 100);
        _lodDist = GameSettings.LodPercentage;
        _lodEffectsDisabled = GameSettings.DisableEffectsOnLOD;
        _taa = GameSettings.EnableTAA;
    }

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(450, 450), ImGuiCond.FirstUseEver);
        
        if (ImGui.Begin("Game Settings", ref _isVisible))
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
            if (ImGui.Button("Apply Render Distance")) { GameSettings.RenderDistance = _renderDist; _renderer.RequestReallocation(); }

            ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Render Scale");
            ImGui.SliderFloat("Render Scale", ref _currentScale, 0.25f, 1.0f, "%.2f");
            if (ImGui.Button("Apply Render Scale")) { GameSettings.RenderScale = _currentScale; _renderer.ApplyRenderScale(); }

            ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Shadows Mode:");
            if (ImGui.RadioButton("None", GameSettings.CurrentShadowMode == ShadowMode.None)) { GameSettings.CurrentShadowMode = ShadowMode.None; _renderer.ReloadShader(); } ImGui.SameLine();
            if (ImGui.RadioButton("Hard", GameSettings.CurrentShadowMode == ShadowMode.Hard)) { GameSettings.CurrentShadowMode = ShadowMode.Hard; _renderer.ReloadShader(); } ImGui.SameLine();
            if (ImGui.RadioButton("Soft", GameSettings.CurrentShadowMode == ShadowMode.Soft)) { GameSettings.CurrentShadowMode = ShadowMode.Soft; _renderer.ReloadShader(); }
            if (GameSettings.CurrentShadowMode == ShadowMode.Soft) { if (ImGui.SliderInt("Soft Samples", ref _shadowSamples, 2, 64)) GameSettings.SoftShadowSamples = _shadowSamples; }
            
            ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Effects");
            if (ImGui.Checkbox("Temporal Anti-Aliasing (TAA)", ref _taa)) GameSettings.EnableTAA = _taa;
            bool ao = GameSettings.EnableAO; if (ImGui.Checkbox("Ambient Occlusion", ref ao)) { GameSettings.EnableAO = ao; _renderer.ReloadShader(); }
            bool water = GameSettings.UseProceduralWater; if (ImGui.Checkbox("Procedural Water (Disable)", ref water)) { GameSettings.UseProceduralWater = water; _renderer.ReloadShader(); }
            bool trans = GameSettings.EnableWaterTransparency; if (ImGui.Checkbox("Water Transparency (Disable)", ref trans)) { GameSettings.EnableWaterTransparency = trans; _renderer.ReloadShader(); }
            bool beam = GameSettings.BeamOptimization; if (ImGui.Checkbox("Beam Optimization", ref beam)) { GameSettings.BeamOptimization = beam; _renderer.ReloadShader(); }
            
            ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Level of Detail (LOD)");
            bool enableLod = GameSettings.EnableLOD;
            if (ImGui.Checkbox("Enable LOD System", ref enableLod)) { GameSettings.EnableLOD = enableLod; _renderer.ReloadShader(); }
            if (enableLod) {
                int lodPercent = (int)(GameSettings.LodPercentage * 100);
                if (ImGui.SliderInt("LOD Start (%)", ref lodPercent, 10, 95)) GameSettings.LodPercentage = lodPercent / 100.0f;
                if (ImGui.Checkbox("Disable Shadows/AO on LOD", ref _lodEffectsDisabled)) GameSettings.DisableEffectsOnLOD = _lodEffectsDisabled;
            }
            
            ImGui.Spacing(); ImGui.Separator(); ImGui.Text("CPU & Threads"); ImGui.Separator();
            if (ImGui.SliderInt("Generation Threads", ref _genThreads, 1, Environment.ProcessorCount)) { GameSettings.GenerationThreads = _genThreads; _worldManager.SetGenerationThreadCount(_genThreads); }
            if (ImGui.SliderInt("Physics Threads", ref _physThreads, 1, Environment.ProcessorCount)) { GameSettings.PhysicsThreads = _physThreads; _worldManager.PhysicsWorld.SetThreadCount(_physThreads); }
            if (ImGui.SliderInt("Main Thread Budget (%)", ref _budgetPercent, 5, 100)) GameSettings.WorldUpdateBudgetPercentage = _budgetPercent / 100.0f;
            ImGui.TextDisabled($"Max available processors: {Environment.ProcessorCount}");
        }
        ImGui.End();
    }
}

public class TimeSettingsWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(350, 160), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Time & Environment", ref _isVisible))
        {
            bool isDynamic = GameSettings.EnableDynamicTime;
            if (ImGui.Checkbox("Enable Dynamic Time", ref isDynamic)) GameSettings.EnableDynamicTime = isDynamic;
            
            float t = GameSettings.TimeOfDay;
            if (ImGui.SliderFloat("Time of Day", ref t, 0.0f, 24.0f, "%.2f (Hours)")) { 
                double fullDays = Math.Floor(GameSettings.TotalTimeHours / 24.0) * 24.0; 
                GameSettings.TotalTimeHours = fullDays + t; 
            }
            
            int days = (int)(GameSettings.TotalTimeHours / 24.0);
            if (ImGui.SliderInt("Passed Days", ref days, 0, 30, "%d days")) { 
                float currentHour = GameSettings.TimeOfDay; 
                GameSettings.TotalTimeHours = (days * 24.0) + currentHour; 
            }
            
            float ts = GameSettings.TimeScale;
            if (ImGui.SliderFloat("Time Scale", ref ts, 0.0f, 5000.0f, "%.0f (Multiplier)")) 
                GameSettings.TimeScale = ts;
        }
        ImGui.End();
    }
}

public class VisualDebugWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(320, 200), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Visual Debug", ref _isVisible))
        {
            bool heatmap = GameSettings.ShowDebugHeatmap; 
            if (ImGui.Checkbox("Engine Step Heatmap", ref heatmap)) GameSettings.ShowDebugHeatmap = heatmap;

            ImGui.Spacing(); ImGui.Separator();
            ImGui.Text("Explosion Debug:");
            bool expRays = GameSettings.ShowExplosionRays;
            if (ImGui.Checkbox("Show Rays", ref expRays)) GameSettings.ShowExplosionRays = expRays;
            
            ImGui.SameLine();
            
            bool expRad = GameSettings.ShowExplosionRadius;
            if (ImGui.Checkbox("Show Radius", ref expRad)) GameSettings.ShowExplosionRadius = expRad;

            ImGui.Spacing(); ImGui.Separator();
            ImGui.Text("Collision Wireframe:");
            bool statCol = GameSettings.ShowStaticCollisions;
            if (ImGui.Checkbox("Static Geometry", ref statCol)) GameSettings.ShowStaticCollisions = statCol;
            
            bool dynCol = GameSettings.ShowDynamicCollisions;
            if (ImGui.Checkbox("Dynamic Objects", ref dynCol)) GameSettings.ShowDynamicCollisions = dynCol;
            
            ImGui.Spacing(); ImGui.Separator();
            if (ImGui.Button("Reset Performance Counters")) PerformanceMonitor.GetDataAndReset(1.0);
        }
        ImGui.End();
    }
}

public class MainToolbarWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }
    
    private readonly Game _game;
    private readonly SettingsWindow _settings;
    private readonly TimeSettingsWindow _timeSettings;
    private readonly VisualDebugWindow _visualDebug;
    private readonly DebugStatsWindow _stats;
    private readonly VoxelInspectorWindow _inspector;

    public MainToolbarWindow(Game game, SettingsWindow settings, TimeSettingsWindow timeSettings, VisualDebugWindow visualDebug, DebugStatsWindow stats, VoxelInspectorWindow inspector)
    {
        _game = game;
        _settings = settings;
        _timeSettings = timeSettings;
        _visualDebug = visualDebug;
        _stats = stats;
        _inspector = inspector;
    }

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;

        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.MenuItem("Resume Game")) IsVisible = false;

            if (ImGui.BeginMenu("Windows"))
            {
                bool setVis = _settings.IsVisible;
                if (ImGui.MenuItem("Game Settings", "", ref setVis)) _settings.IsVisible = setVis;

                bool timeVis = _timeSettings.IsVisible;
                if (ImGui.MenuItem("Time & Environment", "", ref timeVis)) _timeSettings.IsVisible = timeVis;

                bool visVis = _visualDebug.IsVisible;
                if (ImGui.MenuItem("Visual Debug", "", ref visVis)) _visualDebug.IsVisible = visVis;

                bool statVis = _stats.IsVisible;
                if (ImGui.MenuItem("Performance Stats", "", ref statVis)) _stats.IsVisible = statVis;

                bool inspVis = _inspector.IsVisible;
                if (ImGui.MenuItem("Voxel Inspector", "", ref inspVis)) _inspector.IsVisible = inspVis;

                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("Quit to Desktop")) _game.Close();

            ImGui.EndMainMenuBar();
        }
    }
}