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
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(320, 200), ImGuiCond.FirstUseEver);

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
        }
        ImGui.End();
    }
}