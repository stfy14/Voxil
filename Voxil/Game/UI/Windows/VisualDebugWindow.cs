// --- START OF FILE VisualDebugWindow.cs ---
using ImGuiNET;

public class VisualDebugWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(380, 0), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Visual Debug", ref _isVisible))
        {
            bool heatmap = GameSettings.ShowDebugHeatmap;
            if (ImGui.Checkbox("Engine Step Heatmap", ref heatmap))
                GameSettings.ShowDebugHeatmap = heatmap;

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

            ImGui.TextColored(new System.Numerics.Vector4(0.97f, 0.82f, 0.2f, 1), "GI Probe Debug:");
            if (GameSettings.EnableGI)
            {
                bool showProbes = GameSettings.ShowGIProbes;
                if (ImGui.Checkbox("Visualize Probe Grid", ref showProbes))
                    GameSettings.ShowGIProbes = showProbes;

                if (showProbes)
                {
                    ImGui.SameLine(); ImGui.TextDisabled("(цвет = GI, серый = мертвый)");
                    bool xray = GameSettings.ShowGIProbesXRay;
                    if (ImGui.Checkbox("X-Ray Mode (сквозь стены)", ref xray))
                        GameSettings.ShowGIProbesXRay = xray;
                    ImGui.Spacing();

                    string[] lodNames = { "L0 — Near (1м)", "L1 — Mid (4м)", "L2 — Far (16м)", "Все уровни" };
                    int lodIdx = GameSettings.GIDebugLOD == -1 ? 3 : GameSettings.GIDebugLOD;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.Combo("LOD уровень", ref lodIdx, lodNames, lodNames.Length))
                        GameSettings.GIDebugLOD = lodIdx == 3 ? -1 : lodIdx;
                    ImGui.Spacing();

                    ImGui.TextColored(new System.Numerics.Vector4(0.9f, 0.8f, 0.2f, 1f), "■");
                    ImGui.SameLine(); ImGui.TextDisabled($"L0: {GIProbeSystem.RAYS_PER_PROBE_L0} rays, {GIProbeSystem.PROBES_PER_FRAME_L0}/frame");

                    ImGui.TextColored(new System.Numerics.Vector4(0.2f, 0.8f, 0.9f, 1f), "■");
                    ImGui.SameLine(); ImGui.TextDisabled($"L1: {GIProbeSystem.RAYS_PER_PROBE_L1} rays, {GIProbeSystem.PROBES_PER_FRAME_L1}/frame");

                    ImGui.TextColored(new System.Numerics.Vector4(0.8f, 0.2f, 0.9f, 1f), "■");
                    ImGui.SameLine(); ImGui.TextDisabled($"L2: {GIProbeSystem.RAYS_PER_PROBE_L2} rays, {GIProbeSystem.PROBES_PER_FRAME_L2}/frame");
                }

                // --- НОВАЯ ГАЛОЧКА ДЛЯ ГРАНИЦ СЕТОК ---
                ImGui.Spacing();
                bool showBounds = GameSettings.ShowGIProbeGridBounds;
                if (ImGui.Checkbox("Show Grid Bounds (Cascades)", ref showBounds))
                    GameSettings.ShowGIProbeGridBounds = showBounds;
                // --------------------------------------

                ImGui.Spacing();
                int probeCount = GIProbeSystem.PROBE_COUNT;
                ImGui.TextDisabled($"Всего зондов: {probeCount * 3:N0}  ({probeCount:N0} на уровень)");
                ImGui.TextDisabled($"Сетка: {GIProbeSystem.PROBE_X}×{GIProbeSystem.PROBE_Y}×{GIProbeSystem.PROBE_Z}");
                ImGui.Spacing();
                ImGui.TextDisabled("Слот [3] = GlowBall  →  бросить LMB");
                ImGui.TextDisabled("F6 = GI diagnostic   F7 = Spawn GlowBall");
            }
            else
            {
                ImGui.TextDisabled("GI отключён. Включить в Game Settings.");
            }
        }
        ImGui.End();
    }
}