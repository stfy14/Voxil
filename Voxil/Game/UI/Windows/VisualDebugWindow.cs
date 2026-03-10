// --- Game/UI/Windows/VisualDebugWindow.cs ---
// Обновлён для отображения 3 LOD уровней зондов
using ImGuiNET;

public class VisualDebugWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(360, 0), ImGuiCond.FirstUseEver);

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
            ImGui.TextColored(new System.Numerics.Vector4(0.97f, 0.82f, 0.2f, 1), "GI Debug (3-Level LOD):");
            if (GameSettings.EnableGI)
            {
                bool showProbes = GameSettings.ShowGIProbes;
                if (ImGui.Checkbox("Visualize Probe Grid (all levels)", ref showProbes))
                    GameSettings.ShowGIProbes = showProbes;

                int probeCount = GIProbeSystem.PROBE_COUNT;
                int totalProbes = probeCount * 3; // 3 уровня

                ImGui.Spacing();
                // L0
                ImGui.TextColored(new System.Numerics.Vector4(0.9f, 0.8f, 0.2f, 1f), "L0 (Near):");
                ImGui.SameLine();
                ImGui.TextDisabled($"spacing={GIProbeSystem.PROBE_SPACING_L0}m  range=~{GIProbeSystem.PROBE_X * GIProbeSystem.PROBE_SPACING_L0 / 2:F0}m  rays={GIProbeSystem.RAYS_PER_PROBE_L0}  {GIProbeSystem.PROBES_PER_FRAME_L0}/frame");

                // L1
                ImGui.TextColored(new System.Numerics.Vector4(0.2f, 0.8f, 0.9f, 1f), "L1 (Mid): ");
                ImGui.SameLine();
                ImGui.TextDisabled($"spacing={GIProbeSystem.PROBE_SPACING_L1}m  range=~{GIProbeSystem.PROBE_X * GIProbeSystem.PROBE_SPACING_L1 / 2:F0}m  rays={GIProbeSystem.RAYS_PER_PROBE_L1}  {GIProbeSystem.PROBES_PER_FRAME_L1}/frame");

                // L2
                ImGui.TextColored(new System.Numerics.Vector4(0.8f, 0.2f, 0.9f, 1f), "L2 (Far): ");
                ImGui.SameLine();
                ImGui.TextDisabled($"spacing={GIProbeSystem.PROBE_SPACING_L2}m  range=~{GIProbeSystem.PROBE_X * GIProbeSystem.PROBE_SPACING_L2 / 2:F0}m  rays={GIProbeSystem.RAYS_PER_PROBE_L2}  {GIProbeSystem.PROBES_PER_FRAME_L2}/frame");

                ImGui.Spacing();
                ImGui.TextDisabled($"Total probes: {totalProbes:N0}  ({probeCount:N0} per level)");
                ImGui.TextDisabled($"Grid per level: {GIProbeSystem.PROBE_X}x{GIProbeSystem.PROBE_Y}x{GIProbeSystem.PROBE_Z}");

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