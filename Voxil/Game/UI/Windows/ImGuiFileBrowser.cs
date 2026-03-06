// --- Game/UI/ImGuiFileBrowser.cs ---
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

public enum FileBrowserMode { Open, Save }

public class ImGuiFileBrowser
{
    // Состояние
    private bool _isOpen = false;
    private string _currentDir;
    private string _selectedFile = "";
    private string _inputFileName = "my_model.json";
    private string _filter; // например ".json"
    private bool _scrollToTop;
    private FileBrowserMode _mode;

    // Содержимое текущей папки
    private List<string> _dirs  = new();
    private List<string> _files = new();

    // Результат
    public bool Confirmed { get; set; }
    public string ResultPath { get; private set; }

    // Сохранение последней папки
    private static readonly string _settingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "editor_browser.txt");

    private string _errorMessage = "";

    public ImGuiFileBrowser(string filter = ".json")
    {
        _filter     = filter;
        _currentDir = LoadLastDir();
    }

    // =========================================================
    // PUBLIC API
    // =========================================================

    public void Open(FileBrowserMode mode, string defaultFileName = "")
    {
        _isOpen   = true;
        _mode     = mode;
        Confirmed = false;
        ResultPath = "";
        _errorMessage = "";

        if (!string.IsNullOrEmpty(defaultFileName))
            _inputFileName = Path.GetFileName(defaultFileName);

        RefreshContents();
        _scrollToTop = true; // сбрасываем скролл при каждом открытии
    }

    /// <summary>
    /// Вызывай каждый кадр в Draw(). Возвращает true пока окно открыто.
    /// Когда закрылось — проверяй Confirmed и ResultPath.
    /// </summary>
    public bool Draw()
    {
        if (!_isOpen) return false;

        ImGui.SetNextWindowSize(new Vector2(600, 400), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(
            (ImGui.GetIO().DisplaySize - new Vector2(600, 400)) * 0.5f,
            ImGuiCond.FirstUseEver);

        string title = _mode == FileBrowserMode.Open ? "Open File###FileBrowser"
                                                     : "Save File###FileBrowser";

        bool open = true;
        if (!ImGui.Begin(title, ref open,
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking))
        {
            ImGui.End();
            if (!open) _isOpen = false;
            return _isOpen;
        }

        DrawPathBar();
        ImGui.Separator();
        DrawContents();
        ImGui.Separator();
        DrawBottomBar();

        ImGui.End();

        if (!open) _isOpen = false;
        return _isOpen;
    }

    // =========================================================
    // РИСОВАНИЕ
    // =========================================================

    private void DrawPathBar()
    {
        // Разбиваем путь на части — кликабельные сегменты
        ImGui.TextDisabled("Location: ");
        ImGui.SameLine();

        var parts = GetPathParts(_currentDir);
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) { ImGui.SameLine(); ImGui.TextDisabled("/"); ImGui.SameLine(); }

            if (ImGui.SmallButton(parts[i].Label + $"##pb{i}"))
            {
                NavigateTo(parts[i].FullPath);
            }
        }

        // Кнопка "вверх"
        ImGui.SameLine();
        ImGui.TextDisabled("  ");
        ImGui.SameLine();
        string parent = Directory.GetParent(_currentDir)?.FullName;
        if (parent != null)
        {
            if (ImGui.SmallButton("↑ Up"))
                NavigateTo(parent);
        }
    }

    private void DrawContents()
    {
        float reserveBottom = 60f;
        ImGui.BeginChild("##contents", new Vector2(0, -reserveBottom));

        // ✅ ИСПРАВЛЕНИЕ БАГ 3 (часть 1):
        // _scrollToTop устанавливался в NavigateTo(), но НИКОГДА не применялся —
        // флаг просто игнорировался. Теперь сбрасываем скролл в начало списка
        // сразу после смены директории (внутри BeginChild, до рендера элементов).
        if (_scrollToTop)
        {
            ImGui.SetScrollHereY(0.0f);
            _scrollToTop = false;
        }

        string navigateTo = null;

        // Папки
        foreach (var dir in _dirs)
        {
            string name = Path.GetFileName(dir);
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.85f, 0.4f, 1.0f));
            if (ImGui.Selectable($"[D] {name}##d", false, ImGuiSelectableFlags.AllowDoubleClick))
            {
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    navigateTo = dir;
            }
            ImGui.PopStyleColor();
        }

        // Файлы
        foreach (var file in _files)
        {
            string name = Path.GetFileName(file);
            bool selected = _selectedFile == file;

            if (ImGui.Selectable($"[F] {name}##f", selected, ImGuiSelectableFlags.AllowDoubleClick))
            {
                _selectedFile  = file;
                _inputFileName = name;

                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    navigateTo = null; // двойной клик по файлу — Confirm
            }
        }

        ImGui.EndChild();

        // Навигируем после цикла
        if (navigateTo != null)
            NavigateTo(navigateTo);
        else if (_selectedFile != "" && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            Confirm();
    }

    private void DrawBottomBar()
    {
        ImGui.Spacing();

        // Поле имени файла
        ImGui.SetNextItemWidth(-120);
        ImGui.InputText("##filename", ref _inputFileName, 256);

        ImGui.SameLine();

        string actionLabel = _mode == FileBrowserMode.Open ? "Open" : "Save";
        if (ImGui.Button(actionLabel, new Vector2(55, 0)))
            Confirm();

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(55, 0)))
            _isOpen = false;

        if (!string.IsNullOrEmpty(_errorMessage))
        {
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), _errorMessage);
        }
    }

    // =========================================================
    // ЛОГИКА
    // =========================================================

    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(_inputFileName))
        {
            _errorMessage = "Enter a file name.";
            return;
        }

        string fileName = _inputFileName;
        if (!fileName.EndsWith(_filter, StringComparison.OrdinalIgnoreCase))
            fileName += _filter;

        string fullPath = Path.Combine(_currentDir, fileName);

        if (_mode == FileBrowserMode.Open && !File.Exists(fullPath))
        {
            _errorMessage = $"File not found: {fileName}";
            return;
        }

        ResultPath = fullPath;
        Confirmed  = true;
        _isOpen    = false;
        SaveLastDir(_currentDir);
    }

    private void NavigateTo(string path)
    {
        if (!Directory.Exists(path)) return;
        _currentDir   = path;
        _selectedFile = "";
        _errorMessage = "";
        RefreshContents();
        _scrollToTop = true; // флаг — теперь реально используется в DrawContents
    }

    private void RefreshContents()
    {
        _dirs.Clear();
        _files.Clear();

        try
        {
            foreach (var d in Directory.GetDirectories(_currentDir))
                _dirs.Add(d);

            foreach (var f in Directory.GetFiles(_currentDir, $"*{_filter}"))
                _files.Add(f);

            _dirs.Sort(StringComparer.OrdinalIgnoreCase);
            _files.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _errorMessage = $"Cannot read directory: {ex.Message}";
        }
    }

    // =========================================================
    // ВСПОМОГАТЕЛЬНОЕ
    // =========================================================

    private struct PathPart { public string Label; public string FullPath; }

    private List<PathPart> GetPathParts(string path)
    {
        var parts = new List<PathPart>();
        var di = new DirectoryInfo(path);
        var stack = new Stack<PathPart>();

        while (di != null)
        {
            stack.Push(new PathPart
            {
                Label    = string.IsNullOrEmpty(di.Name) ? di.FullName : di.Name,
                FullPath = di.FullName
            });
            di = di.Parent;
        }

        while (stack.Count > 0)
            parts.Add(stack.Pop());

        return parts;
    }

    private string LoadLastDir()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string saved = File.ReadAllText(_settingsPath).Trim();
                if (Directory.Exists(saved)) return saved;
            }
        }
        catch { }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void SaveLastDir(string dir)
    {
        try { File.WriteAllText(_settingsPath, dir); }
        catch { }
    }
}