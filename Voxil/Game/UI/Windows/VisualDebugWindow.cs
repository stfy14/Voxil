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
                ImGui.TextDisabled("Voxel Cone Tracing (VCT) is Active");
                ImGui.Spacing();
                ImGui.TextColored(new System.Numerics.Vector4(0.9f, 0.8f, 0.2f, 1f), "■ L0 Cascade:");
                ImGui.SameLine(); ImGui.TextDisabled("128x128x128m (High Detail)");

                ImGui.TextColored(new System.Numerics.Vector4(0.2f, 0.8f, 0.9f, 1f), "■ L1 Cascade:");
                ImGui.SameLine(); ImGui.TextDisabled("512x512x512m (Mid Detail)");

                ImGui.TextColored(new System.Numerics.Vector4(0.8f, 0.2f, 0.9f, 1f), "■ L2 Cascade:");
                ImGui.SameLine(); ImGui.TextDisabled("2048x2048x2048m (Far Detail)");
            }
            else
            {
                ImGui.TextDisabled("GI отключён. Включить в Game Settings.");
            }
        }
        ImGui.End();
    }
}