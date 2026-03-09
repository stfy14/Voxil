// --- Game/UI/Windows/InventoryWindow.cs ---
// Изменение: добавлен label "Glow" для слота 2
using ImGuiNET;
using System.Numerics;

public class InventoryWindow : IUIWindow
{
    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }
    private readonly Player _player;

    public InventoryWindow(Player player) { _player = player; }
    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;

        var io = ImGui.GetIO();
        float slotSize    = 50.0f;
        float spacing     = 4.0f;
        int   slotsCount  = 9;
        float windowWidth = (slotSize * slotsCount) + (spacing * (slotsCount - 1)) + 20.0f;
        float windowHeight = slotSize + 20.0f;

        ImGui.SetNextWindowPos(new Vector2((io.DisplaySize.X - windowWidth) * 0.5f, io.DisplaySize.Y - windowHeight - 10.0f));
        ImGui.SetNextWindowSize(new Vector2(windowWidth, windowHeight));

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.1f, 0.1f, 0.1f, 0.7f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 5.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing, 0));
        
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("Hotbar", flags))
        {
            for (int i = 0; i < slotsCount; i++)
            {
                bool isSelected = (_player.SelectedSlot == i);
                
                if (isSelected)
                {
                    ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1, 1, 1, 1));
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 3.0f);
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.4f, 0.4f, 0.4f, 0.5f));
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
                }

                ImGui.PushID(i);
                ImGui.Button("", new Vector2(slotSize, slotSize));
                
                // Подпись слота — ← добавлен "Glow" для слота 2
                string item = i switch
                {
                    0 => "Hand",
                    1 => "TNT",
                    2 => "Glow",  // ← НОВЫЙ
                    _ => ""
                };

                if (!string.IsNullOrEmpty(item))
                {
                    var rectMin  = ImGui.GetItemRectMin();
                    var rectMax  = ImGui.GetItemRectMax();
                    var center   = (rectMin + rectMax) * 0.5f;
                    var textSize = ImGui.CalcTextSize(item);
                    
                    // Желтый цвет для GlowBall
                    uint textColor = (i == 2) ? 0xFF33EEFF : 0xFFFFFFFF;
                    ImGui.GetWindowDrawList().AddText(center - textSize * 0.5f, textColor, item);
                }

                ImGui.PopID();
                ImGui.PopStyleVar();
                ImGui.PopStyleColor();

                if (i < slotsCount - 1) ImGui.SameLine();
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }
}