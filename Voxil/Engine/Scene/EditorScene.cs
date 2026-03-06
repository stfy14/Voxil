// --- Game/Scenes/EditorScene.cs ---
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;

using ImGuiNET;

public class EditorScene : IScene
{
    private readonly OrbitalCamera _camera;
    private VoxelObject _model;
    private readonly GpuRaycastingRenderer _renderer;
    private int _screenWidth;
    private int _screenHeight;

    // Параметры сетки
    private int _gridSize    = 8;
    private float _voxelSize = 1.0f;

    // Активный инструмент
    public enum Tool { Edit, Paint }
    private Tool _activeTool = Tool.Edit;
    private MaterialType _activeMaterial = MaterialType.Stone;

    // Состояние мыши для орбиты
    private bool _isRotating = false;
    private bool _isPanning  = false;

    // Колбэк для выхода обратно в игру
    public event Action OnExitRequested;
    
    private readonly IVoxelObjectService _objectService = ServiceLocator.Get<IVoxelObjectService>();
    
    public EditorScene(GpuRaycastingRenderer renderer, float aspectRatio, int screenWidth, int screenHeight)
    {
        _renderer     = renderer;
        _screenWidth  = screenWidth;
        _screenHeight = screenHeight;
        _camera       = new OrbitalCamera();
        _camera.AspectRatio = aspectRatio;
    }

    public void OnEnter()
    {
        Console.WriteLine("[EditorScene] Entered.");
        CreateNewModel(_gridSize, _voxelSize); 
        // FocusOn уже вызывается внутри CreateNewModel — повтор не нужен
    }

    public void OnExit()
    {
        _renderer.SetEditorModel(null);
        Console.WriteLine("[EditorScene] Exited.");
    }

    public void Update(float deltaTime, InputManager input)
    {
        HandleCameraInput(input);
        UpdateHover(input);
        HandleEditorInput(input, _screenWidth, _screenHeight);
    }

    public void Render()
    {
        if (_model == null) return;
        _model.ForceSetTransform(Vector3.Zero, Quaternion.Identity);
        _renderer.UpdateDynamicObjectsAndGrid(_camera.Target);
        
        _renderer.Render(CameraData.From(_camera));
    }

    public void OnResize(int width, int height)
    {
        _screenWidth  = width;
        _screenHeight = height;
        _camera.AspectRatio = (float)width / height;
        _renderer.OnResize(width, height);
    }

    // =========================================================
    // СОЗДАНИЕ / ЗАГРУЗКА / СОХРАНЕНИЕ
    // =========================================================

    public void CreateNewModel(int gridSize, float voxelSize)
    {
        _gridSize  = gridSize;
        _voxelSize = voxelSize;

        // Тестовый куб 2x2x2
        var coords = new List<Vector3i>
        {
            new(0,0,0), new(1,0,0),
            new(0,1,0), new(1,1,0),
            new(0,0,1), new(1,0,1),
            new(0,1,1), new(1,1,1),
        };

        _model = new VoxelObject(coords, MaterialType.Stone, voxelSize);

        // ✅ ИСПРАВЛЕНИЕ БАГ 1:
        // Конструктор VoxelObject вычисляет LocalCenterOfMass через CalculateLocalCenterOfMass(),
        // что смещает модель в GetInterpolatedModelMatrix (-CoM трансляция).
        // В редакторе модель всегда рендерится в мировом начале координат (ForceSetTransform(Zero)),
        // поэтому CoM должен быть нулевым — иначе GetModelCenter() указывает не туда,
        // куда реально смотрит камера, и рейкаст расходится с визуальным положением вокселей.
        _model.LocalCenterOfMass = Vector3.Zero;

        _renderer.SetEditorModel(_model);
        _camera.FocusOn(GetModelCenter(), gridSize * voxelSize);
        Console.WriteLine($"[EditorScene] New model: {gridSize}^3, voxel={voxelSize}");
    }

    public void SaveModel(string path)
        => VoxelModelSerializer.Save(_model, path);

    public void LoadModel(string path)
    {
        _model = VoxelModelSerializer.Load(path);
        _model.RecalculateBoundsPublic();
        _model.LocalCenterOfMass = Vector3.Zero; // уже было — оставляем
        _renderer.SetEditorModel(_model);
        _camera.FocusOn(GetModelCenter(), _model.LocalBoundsMax.Length);
        Console.WriteLine($"[EditorScene] Loaded: {path}");
    }

    // =========================================================
    // ВВОД КАМЕРЫ
    // =========================================================

    private void HandleCameraInput(InputManager input)
    {
        // ✅ ИСПРАВЛЕНИЕ БАГ 2 (и частично БАГ 3):
        // Раньше камера реагировала на ввод даже когда ImGui захватывал мышь
        // (открытое меню, FileBrowser, любое ImGui-окно).
        // Это приводило к тому, что:
        //   - Скролл в FileBrowser одновременно зумировал сцену
        //   - ViewMatrix менялась во время UI-взаимодействия, сдвигая рейкаст
        if (ImGui.GetIO().WantCaptureMouse) return;

        var mouseDelta = input.GetRawMouseDelta();
        bool middleDown = input.IsMouseButtonDown(MouseButton.Middle);
        bool shiftDown  = input.IsKeyDown(Keys.LeftShift);

        if (middleDown && shiftDown)
            _camera.Pan(mouseDelta.X, mouseDelta.Y);
        else if (middleDown)
            _camera.Rotate(mouseDelta.X, mouseDelta.Y);

        float scroll = input.GetScrollDelta();
        if (MathF.Abs(scroll) > 0.01f)
            _camera.Zoom(scroll);
    }

    // =========================================================
    // ВВОД РЕДАКТОРА
    // =========================================================

    private void HandleEditorInput(InputManager input, int screenWidth, int screenHeight)
    {
        // Не обрабатываем клики если ImGui перехватывает мышь
        if (ImGui.GetIO().WantCaptureMouse) return;

        bool leftClick  = input.IsMouseButtonPressed(MouseButton.Left);
        bool rightClick = input.IsMouseButtonPressed(MouseButton.Right);

        if (!leftClick && !rightClick) return;

        var mousePos = input.GetMousePosition();
        
        var (rayOrigin, rayDir) = EditorRaycast.ScreenToRay(mousePos, screenWidth, screenHeight, _camera);

        bool hit = EditorRaycast.RaycastModel(rayOrigin, rayDir, _model, out var hitVoxel, out var hitNormal);

        if (!hit) return;

        if (leftClick)
        {
            if (_activeTool == Tool.Paint)
            {
                _model.VoxelMaterials[hitVoxel] = (uint)_activeMaterial;
                _model.SvoDirty = true;
                _renderer.SetEditorModel(_model);
            }
            else // Tool.Edit — добавляем
            {
                var newPos = hitVoxel + hitNormal;

                if (!_model.VoxelCoordinates.Contains(newPos))
                {
                    _model.VoxelCoordinates.Add(newPos);
                    _model.VoxelMaterials[newPos] = (uint)_activeMaterial;
                    NormalizeModelCoords();
                    _model.RecalculateBoundsPublic();
                    _model.SvoDirty = true;
                    _renderer.SetEditorModel(_model);
                }
            }
        }
        else if (rightClick && _activeTool == Tool.Edit)
        {
            _model.RemoveVoxel(hitVoxel);
            _model.RecalculateBoundsPublic();
            _model.SvoDirty = true;
            _renderer.SetEditorModel(_model);
        }
    }

    // =========================================================
    // GUI/UI
    // =========================================================
    
    private void UpdateHover(InputManager input)
    {
        var mousePos = input.GetMousePosition();
        var (ro, rd) = EditorRaycast.ScreenToRay(mousePos, _screenWidth, _screenHeight, _camera);
        bool hovered = EditorRaycast.RaycastModel(ro, rd, _model, out var hoveredVoxel, out _);

        if (hovered)
        {
            // ИСПОЛЬЗУЕМ CONSTANTS, так как шейдер проверяет localHit через invModel
            var min = new Vector3(hoveredVoxel.X, hoveredVoxel.Y, hoveredVoxel.Z) * Constants.VoxelSize;
            _renderer.SetHoverVoxel(
                min - new Vector3(0.0001f), 
                min + new Vector3(Constants.VoxelSize + 0.0001f)
            );
        }
        else
        {
            _renderer.ClearHoverVoxel();
        }
    }
    
    // =========================================================
    // ВСПОМОГАТЕЛЬНОЕ
    // =========================================================

    private Vector3 GetModelCenter()
    {
        if (_model == null) return Vector3.Zero;
    
        // Центр в локальных координатах
        Vector3 localCenter = (_model.LocalBoundsMin + _model.LocalBoundsMax) * 0.5f;
    
        // Переводим в мировые координаты, применяя Scale модели
        return localCenter * _model.Scale; 
    }
    
    private void NormalizeModelCoords()
    {
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        foreach (var v in _model.VoxelCoordinates)
        {
            if (v.X < minX) minX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.Z < minZ) minZ = v.Z;
        }

        if (minX >= 0 && minY >= 0 && minZ >= 0) return;

        var offset = new Vector3i(
            minX < 0 ? -minX : 0,
            minY < 0 ? -minY : 0,
            minZ < 0 ? -minZ : 0);

        for (int i = 0; i < _model.VoxelCoordinates.Count; i++)
            _model.VoxelCoordinates[i] += offset;

        var newMats = new Dictionary<Vector3i, uint>();
        foreach (var kvp in _model.VoxelMaterials)
            newMats[kvp.Key + offset] = kvp.Value;
        _model.VoxelMaterials.Clear();
        foreach (var kvp in newMats)
            _model.VoxelMaterials[kvp.Key] = kvp.Value;

        // Компенсируем сдвиг в камере
        var worldOffset = new Vector3(offset.X, offset.Y, offset.Z) * Constants.VoxelSize * _model.Scale;
        _camera.Target += worldOffset;
    }

    public OrbitalCamera Camera      => _camera;
    public VoxelObject   Model       => _model;
    public Tool          ActiveTool  { get => _activeTool;    set => _activeTool = value; }
    public MaterialType  ActiveMaterial { get => _activeMaterial; set => _activeMaterial = value; }
    public int           GridSize    { get => _gridSize; }
    public float         VoxelSize   { get => _voxelSize; }
}