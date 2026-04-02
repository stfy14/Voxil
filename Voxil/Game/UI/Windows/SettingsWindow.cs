// --- Game/UI/Windows/SettingsWindow.cs ---
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
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(450, 500), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Game Settings", ref _isVisible))
        {
            ImGui.Text("Graphics");
            ImGui.Separator();

            long totalVramBytes = (long)_renderer.TotalVramMb * 1024 * 1024;
            long safeBudget     = Math.Max(0, totalVramBytes - (2500L * 1024 * 1024));
            long futureBytes    = _renderer.CalculateMemoryBytesForDistance(_renderDist);
            long currentBytes   = _renderer.CurrentAllocatedBytes;
            bool danger         = futureBytes > safeBudget;

            if (danger) ImGui.PushStyleColor(ImGuiCol.SliderGrab, new System.Numerics.Vector4(1, 0, 0, 1));
            ImGui.SliderInt("Render Distance", ref _renderDist, 4, 128);
            if (danger) ImGui.PopStyleColor();

            float futureMb  = futureBytes  / (1024f * 1024f);
            float currentMb = currentBytes / (1024f * 1024f);
            ImGui.TextDisabled($"VRAM: {currentMb:F0} MB used → {futureMb:F0} MB estimated");

            if (ImGui.Button("Apply Render Distance"))
            {
                GameSettings.RenderDistance = _renderDist;
                _renderer.RequestReallocation();
            }

            ImGui.Spacing();

            if (ImGui.SliderFloat("Render Scale", ref _currentScale, 0.25f, 1.0f, "%.2f"))
            {
                GameSettings.RenderScale = _currentScale;
                _renderer.ApplyRenderScale();
            }

            bool taa = _taa;
            if (ImGui.Checkbox("TAA (Anti-Aliasing)", ref taa)) { _taa = taa; GameSettings.EnableTAA = taa; }

            ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Shadows");
            ImGui.Separator();

            bool ao = GameSettings.EnableAO;
            if (ImGui.Checkbox("Ambient Occlusion", ref ao)) { GameSettings.EnableAO = ao; _renderer.ReloadShader(); }

            int downscaleIdx = GameSettings.ShadowDownscale switch { 1 => 0, 4 => 2, _ => 1 };
            string[] shadowResModes = { "100% (Ultra)", "50% (Balanced)", "25% (Performance)" };

            int shadowMode = (int)GameSettings.CurrentShadowMode;
            string[] shadowModes = { "None", "Hard", "Soft" };
            if (ImGui.Combo("Shadow Mode", ref shadowMode, shadowModes, shadowModes.Length))
            {
                GameSettings.CurrentShadowMode = (ShadowMode)shadowMode;
                _renderer.ReloadShader();
            }

            if (ImGui.Combo("Shadow Resolution", ref downscaleIdx, shadowResModes, shadowResModes.Length))
            {
                GameSettings.ShadowDownscale = downscaleIdx switch { 0 => 1, 2 => 4, _ => 2 };
                _renderer.ApplyRenderScale();
            }

            if (GameSettings.CurrentShadowMode == ShadowMode.Soft)
            {
                if (ImGui.SliderInt("Soft Shadow Samples", ref _shadowSamples, 1, 32))
                    GameSettings.SoftShadowSamples = _shadowSamples;
            }

            ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Water");
            ImGui.Separator();

            bool waterTransp = GameSettings.EnableWaterTransparency;
            if (ImGui.Checkbox("Water Transparency", ref waterTransp)) { GameSettings.EnableWaterTransparency = waterTransp; _renderer.ReloadShader(); }

            bool procWater = GameSettings.UseProceduralWater;
            if (ImGui.Checkbox("Procedural Water", ref procWater)) { GameSettings.UseProceduralWater = procWater; _renderer.ReloadShader(); }

            bool beam = GameSettings.BeamOptimization;
            if (ImGui.Checkbox("Beam Optimization", ref beam)) { GameSettings.BeamOptimization = beam; _renderer.ReloadShader(); }

            // ================================================================
            // GLOBAL ILLUMINATION
            // ================================================================
            ImGui.Spacing(); ImGui.Separator();
            ImGui.TextColored(new System.Numerics.Vector4(0.97f, 0.82f, 0.2f, 1f), "Global Illumination");
            ImGui.Separator();

            bool gi = GameSettings.EnableGI;
            if (ImGui.Checkbox("Enable GI Probes", ref gi))
            {
                GameSettings.EnableGI = gi;
                _renderer.ReloadShader();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Enables probe-based indirect lighting.\nRequires shader reload (automatic).");
            // ================================================================
            ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Level of Detail (LOD)");
            ImGui.Separator();

            bool enableLod = GameSettings.EnableLOD;
            if (ImGui.Checkbox("Enable LOD System", ref enableLod)) { GameSettings.EnableLOD = enableLod; _renderer.ReloadShader(); }

            if (enableLod)
            {
                int lodPercent = (int)(GameSettings.LodPercentage * 100);
                if (ImGui.SliderInt("LOD Start (%)", ref lodPercent, 10, 95))
                    GameSettings.LodPercentage = lodPercent / 100.0f;
                if (ImGui.Checkbox("Disable Shadows/AO on LOD", ref _lodEffectsDisabled))
                    GameSettings.DisableEffectsOnLOD = _lodEffectsDisabled;
            }

            ImGui.Spacing(); ImGui.Separator(); ImGui.Text("CPU & Threads");
            ImGui.Separator();

            if (ImGui.SliderInt("Generation Threads", ref _genThreads, 1, Environment.ProcessorCount))
            { GameSettings.GenerationThreads = _genThreads; _worldManager.SetGenerationThreadCount(_genThreads); }

            if (ImGui.SliderInt("Physics Threads", ref _physThreads, 1, Environment.ProcessorCount))
            { GameSettings.PhysicsThreads = _physThreads; _worldManager.PhysicsWorld.SetThreadCount(_physThreads); }

            if (ImGui.SliderInt("Main Thread Budget (%)", ref _budgetPercent, 5, 100))
                GameSettings.WorldUpdateBudgetPercentage = _budgetPercent / 100.0f;

            ImGui.TextDisabled($"Max available processors: {Environment.ProcessorCount}");

            ImGui.Spacing(); ImGui.Separator();
            if (ImGui.Button("Reset Performance Counters")) PerformanceMonitor.GetDataAndReset(1.0);
        }
        ImGui.End();
    }
}