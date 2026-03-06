using ImGuiNET;
using System.Numerics;

public class InventoryWindow : IUIWindow
{
    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }
    private readonly Player _player;

    public InventoryWindow(Player player)
    {
        _player = player;
    }

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;

        var io = ImGui.GetIO();
        
        float slotSize = 50.0f;
        float spacing = 4.0f; // Расстояние между квадратами
        int slotsCount = 9;
        
        // Вычисляем ширину окна точно по содержимому
        float windowWidth = (slotSize * slotsCount) + (spacing * (slotsCount - 1)) + 20.0f;
        float windowHeight = slotSize + 20.0f;

        ImGui.SetNextWindowPos(new Vector2((io.DisplaySize.X - windowWidth) * 0.5f, io.DisplaySize.Y - windowHeight - 10.0f));
        ImGui.SetNextWindowSize(new Vector2(windowWidth, windowHeight));

        // Стилизация
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.1f, 0.1f, 0.1f, 0.7f)); // Темная подложка
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 5.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing, 0)); // Фиксируем отступ
        
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("Hotbar", flags))
        {
            for (int i = 0; i < slotsCount; i++)
            {
                bool isSelected = (_player.SelectedSlot == i);
                
                // Цвет рамки
                if (isSelected)
                {
                    ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1, 1, 1, 1));
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 3.0f); // Жирная рамка
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.4f, 0.4f, 0.4f, 0.5f));
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
                }

                ImGui.PushID(i);
                
                // Рисуем слот
                // Чтобы ItemSpacing сработал корректно, используем Button
                ImGui.Button("", new Vector2(slotSize, slotSize));
                
                // Отрисовка названия поверх
                var item = (i == 0) ? "Hand" : (i == 1 ? "TNT" : "");
                if (!string.IsNullOrEmpty(item))
                {
                    var rectMin = ImGui.GetItemRectMin();
                    var rectMax = ImGui.GetItemRectMax();
                    var center = (rectMin + rectMax) * 0.5f;
                    var textSize = ImGui.CalcTextSize(item);
                    
                    ImGui.GetWindowDrawList().AddText(center - textSize * 0.5f, 0xFFFFFFFF, item);
                }

                ImGui.PopID();
                ImGui.PopStyleVar(); // FrameBorderSize
                ImGui.PopStyleColor(); // Border

                if (i < slotsCount - 1) ImGui.SameLine();
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(2); // ItemSpacing, WindowRounding
        ImGui.PopStyleColor(); // WindowBg
    }
}