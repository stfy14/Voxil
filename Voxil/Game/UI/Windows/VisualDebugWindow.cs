// --- Game/UI/Windows/VisualDebugWindow.cs ---
using ImGuiNET;

public class VisualDebugWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(320, 0), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Visual Debug", ref _isVisible))
        {
            // --- Heatmap ---
            bool heatmap = GameSettings.ShowDebugHeatmap;
            if (ImGui.Checkbox("Engine Step Heatmap", ref heatmap))
                GameSettings.ShowDebugHeatmap = heatmap;

            ImGui.Spacing(); ImGui.Separator();

            // --- Explosion ---
            ImGui.Text("Explosion Debug:");
            bool expRays = GameSettings.ShowExplosionRays;
            if (ImGui.Checkbox("Show Rays", ref expRays)) GameSettings.ShowExplosionRays = expRays;
            ImGui.SameLine();
            bool expRad = GameSettings.ShowExplosionRadius;
            if (ImGui.Checkbox("Show Radius", ref expRad)) GameSettings.ShowExplosionRadius = expRad;

            ImGui.Spacing(); ImGui.Separator();

            // --- Collision Wireframe ---
            ImGui.Text("Collision Wireframe:");
            bool statCol = GameSettings.ShowStaticCollisions;
            if (ImGui.Checkbox("Static Geometry", ref statCol)) GameSettings.ShowStaticCollisions = statCol;
            bool dynCol = GameSettings.ShowDynamicCollisions;
            if (ImGui.Checkbox("Dynamic Objects", ref dynCol)) GameSettings.ShowDynamicCollisions = dynCol;

            ImGui.Spacing(); ImGui.Separator();

            // --- GI Debug Visualization ---
            ImGui.TextColored(new System.Numerics.Vector4(0.97f, 0.82f, 0.2f, 1), "GI Debug:");
            if (GameSettings.EnableGI)
            {
                bool showProbes = GameSettings.ShowGIProbes;
                if (ImGui.Checkbox("Visualize Probe Grid", ref showProbes))
                    GameSettings.ShowGIProbes = showProbes;

                ImGui.TextDisabled($"Grid: {GIProbeSystem.PROBE_X}x{GIProbeSystem.PROBE_Y}x{GIProbeSystem.PROBE_Z} = {GIProbeSystem.PROBE_COUNT} probes");
                ImGui.TextDisabled($"Spacing: {GIProbeSystem.PROBE_SPACING}m  |  Rays/probe: {GIProbeSystem.RAYS_PER_PROBE}");
                ImGui.TextDisabled($"Update: {GIProbeSystem.PROBES_PER_FRAME}/frame  (full cycle: {GIProbeSystem.PROBE_COUNT / GIProbeSystem.PROBES_PER_FRAME} frames)");

                ImGui.Spacing();
                ImGui.TextDisabled("Slot [3] = GlowBall  →  throw with LMB");
                ImGui.TextDisabled("F6 = GI diagnostic   F7 = Spawn GlowBall");
            }
            else
            {
                ImGui.TextDisabled("GI is disabled. Enable it in Game Settings.");
            }
        }
        ImGui.End();
    }
}