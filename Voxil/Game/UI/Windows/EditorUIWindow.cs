// --- Game/UI/Windows/EditorUIWindow.cs ---
using ImGuiNET;
using System;
using System.IO;
using System.Numerics;

public class EditorUIWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    private readonly EditorScene _editor;
    private readonly Action _onExitToGame;

    // File
    private string _filePath = "Models/my_model.json";
    private readonly ImGuiFileBrowser _browser = new ImGuiFileBrowser(".json");
    private FileBrowserMode _pendingMode;

    // Grid settings
    private int _pendingGridSize;
    private float _pendingVoxelSize;

    private static readonly (MaterialType Type, string Label)[] _materials =
    {
        (MaterialType.Stone, "Stone"),
        (MaterialType.Dirt,  "Dirt"),
        (MaterialType.Grass, "Grass"),
        (MaterialType.Wood,  "Wood"),
        (MaterialType.Water, "Water"),
        (MaterialType.TNT,   "TNT"),
    };

    public EditorUIWindow(EditorScene editor, Action onExitToGame)
    {
        _editor           = editor;
        _onExitToGame     = onExitToGame;
        _pendingGridSize  = editor.GridSize;
        _pendingVoxelSize = editor.VoxelSize;
    }

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;

        if (!ImGui.BeginMainMenuBar()) return;

        if (ImGui.MenuItem("Exit to Game"))
            _onExitToGame?.Invoke();

        ImGui.Separator();

        if (ImGui.BeginMenu("File"))
        {
            ImGui.SetNextItemWidth(200);
            ImGui.InputText("##path", ref _filePath, 256);
            ImGui.Spacing();
            if (ImGui.MenuItem("Save As...")) { _pendingMode = FileBrowserMode.Save; _browser.Open(FileBrowserMode.Save, _filePath); }
            if (ImGui.MenuItem("Open..."))    { _pendingMode = FileBrowserMode.Open;  _browser.Open(FileBrowserMode.Open,  _filePath); }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Tools"))
        {
            DrawToolMenuItem("Edit",    EditorScene.Tool.Edit);
            DrawToolMenuItem("Paint",  EditorScene.Tool.Paint);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Material"))
        {
            foreach (var (type, label) in _materials)
            {
                var col = MaterialRegistry.GetColor(type);
                bool selected = _editor.ActiveMaterial == type;
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(col.r, col.g, col.b, 1.0f));
                if (ImGui.MenuItem($" {label}", "", selected))
                    _editor.ActiveMaterial = type;
                ImGui.PopStyleColor();
            }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Grid"))
        {
            ImGui.Text("Grid Size (voxels):");
            ImGui.SetNextItemWidth(120);
            ImGui.SliderInt("##gridsize", ref _pendingGridSize, 2, 64);

            ImGui.Text("Voxel Size (meters):");
            ImGui.SetNextItemWidth(120);
            ImGui.SliderFloat("##voxelsize", ref _pendingVoxelSize, 0.1f, 4.0f, "%.2f");

            ImGui.Spacing();
            if (ImGui.Button("Apply & Reset Model"))
                _editor.CreateNewModel(_pendingGridSize, _pendingVoxelSize);

            ImGui.EndMenu();
        }

        ImGui.Separator();
        ImGui.TextDisabled($"Tool: {_editor.ActiveTool}  |  Mat: {_editor.ActiveMaterial}");

        ImGui.EndMainMenuBar();
        
        // Браузер рисуется вне менюбара
        bool stillOpen = _browser.Draw();
        if (!stillOpen && _browser.Confirmed)
        {
            _filePath = _browser.ResultPath;
            if (_pendingMode == FileBrowserMode.Save) TrySave();
            else TryLoad();
            _browser.Confirmed = false; // ← сбрасываем флаг!
        }
    }

    private void DrawToolMenuItem(string label, EditorScene.Tool tool)
    {
        bool selected = _editor.ActiveTool == tool;
        if (ImGui.MenuItem(label, "", selected))
            _editor.ActiveTool = tool;
    }

    private void TrySave()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            _editor.SaveModel(_filePath);
            Console.WriteLine($"[Editor] Saved: {_filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Editor] Save error: {ex.Message}");
        }
    }

    private void TryLoad()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                Console.WriteLine($"[Editor] File not found: {_filePath}");
                return;
            }
            _editor.LoadModel(_filePath);
            Console.WriteLine($"[Editor] Loaded: {_filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Editor] Load error: {ex.Message}");
        }
    }
}