// --- Game/UI/Windows/TimeSettingsWindow.cs ---
using ImGuiNET;
using System;

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
            if (ImGui.Checkbox("Enable Dynamic Time", ref isDynamic))
                GameSettings.EnableDynamicTime = isDynamic;

            float t = GameSettings.TimeOfDay;
            if (ImGui.SliderFloat("Time of Day", ref t, 0.0f, 24.0f, "%.2f (Hours)"))
            {
                double fullDays = Math.Floor(GameSettings.TotalTimeHours / 24.0) * 24.0;
                GameSettings.TotalTimeHours = fullDays + t;
            }

            int days = (int)(GameSettings.TotalTimeHours / 24.0);
            if (ImGui.SliderInt("Passed Days", ref days, 0, 30, "%d days"))
            {
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