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
    private readonly EditorGridRenderer _gridRenderer;
    private int _screenWidth;
    private int _screenHeight;

    // Настройки
    private int _gridSize    = 16;
    private float _voxelSize = 1.0f;
    
    public bool IsGridLimitEnabled { get; set; } = true;
    public bool IsGridVisible { get; set; } = true;
    public float GridOpacity { get; set; } = 0.4f;

    // Позиция модели в мире (левый нижний угол её bounding box'а)
    private Vector3 _modelWorldPosition = Vector3.Zero; 
    private Vector3 _gridCenter = Vector3.Zero;

    public enum Tool { Edit, Paint }
    private Tool _activeTool = Tool.Edit;
    private MaterialType _activeMaterial = MaterialType.Stone;

    public event Action OnExitRequested;

    public EditorScene(GpuRaycastingRenderer renderer, float aspectRatio, int screenWidth, int screenHeight)
    {
        _renderer     = renderer;
        _screenWidth  = screenWidth;
        _screenHeight = screenHeight;
        _camera       = new OrbitalCamera();
        _camera.AspectRatio = aspectRatio;
        _gridRenderer = new EditorGridRenderer();
    }

    public void OnEnter()
    {
        Console.WriteLine("[EditorScene] Entered.");
        CreateNewModel(_gridSize, _voxelSize); 
    }

    public void OnExit()
    {
        _renderer.SetEditorModel(null);
    }

    public void Update(float deltaTime, InputManager input)
    {
        bool overUI = ImGui.GetIO().WantCaptureMouse;
        
        if (_model != null)
            _model.ForceSetTransform(_modelWorldPosition, Quaternion.Identity);


        if (!overUI)
        {
            HandleCameraInput(input);
            UpdateHover(input);
            HandleEditorInput(input, _screenWidth, _screenHeight);
        }
    }

    public void Render()
    {
        if (_model == null) return;
        _model.ForceSetTransform(_modelWorldPosition, Quaternion.Identity);
        _renderer.UpdateDynamicObjectsAndGrid(Vector3.Zero);
    
        var camData = CameraData.From(_camera);
        _renderer.Render(camData);

        if (IsGridVisible)
        {
            var gridColor = new Vector4(1f, 1f, 1f, GridOpacity);
            _gridRenderer.Render(camData, _gridSize, _voxelSize, gridColor, _gridCenter);
        }
    }

    public void OnResize(int width, int height)
    {
        _screenWidth  = width;
        _screenHeight = height;
        _camera.AspectRatio = (float)width / height;
        _renderer.OnResize(width, height);
    }

    // =========================================================
    // ЛОГИКА МОДЕЛИ
    // =========================================================

    public void CreateNewModel(int gridSize, float voxelSize)
    {
        _gridSize = gridSize;
        _voxelSize = voxelSize;

        // 1. Центрируем рабочую область
        float halfExtent = (gridSize * voxelSize) / 2.0f;
        _modelWorldPosition = new Vector3(-halfExtent, -halfExtent, -halfExtent);
        _gridCenter = Vector3.Zero; 

        // 2. Создаем стартовый воксель (или 2х2)
        var coords = new List<Vector3i>();
        if (gridSize % 2 != 0) 
        {
            int center = gridSize / 2; 
            coords.Add(new Vector3i(center, center, center));
        }
        else 
        {
            int centerHigh = gridSize / 2;
            int centerLow = centerHigh - 1;
            for (int x = centerLow; x <= centerHigh; x++)
            for (int y = centerLow; y <= centerHigh; y++)
            for (int z = centerLow; z <= centerHigh; z++)
                coords.Add(new Vector3i(x, y, z));
        }

        // 3. Создаем модель
        _model = new VoxelObject(coords, MaterialType.Stone, voxelSize);
        _model.LocalCenterOfMass = Vector3.Zero; 
        _model.ForceSetTransform(_modelWorldPosition, Quaternion.Identity);
        _renderer.SetEditorModel(_model);
        _model.RecalculateBoundsPublic();

        // 4. Умная камера: ставим на комфортное расстояние
        FitCameraToModel();
    }

    // =========================================================
    // ВВОД
    // =========================================================

    private void HandleEditorInput(InputManager input, int screenWidth, int screenHeight)
    {
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
            else // Add
            {
                var newPos = hitVoxel + hitNormal;
                TryAddVoxel(newPos);
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

    private void TryAddVoxel(Vector3i pos)
    {
        if (_model.VoxelMaterials.ContainsKey(pos)) return;

        if (IsGridLimitEnabled)
        {
            // Проверяем ЖЕСТКИЕ границы локальной сетки
            // Координаты вокселей не могут быть меньше 0 и больше или равны GridSize
            if (pos.X < 0 || pos.Y < 0 || pos.Z < 0 || 
                pos.X >= _gridSize || pos.Y >= _gridSize || pos.Z >= _gridSize)
            {
                Console.WriteLine($"[Editor] Limit Reached! Out of grid bounds.");
                return;
            }
        }

        _model.VoxelCoordinates.Add(pos);
        _model.VoxelMaterials[pos] = (uint)_activeMaterial;
    
        // ВАЖНО: Я убрал отсюда NormalizeModelCoords(); 
        // Внутри редактора координаты вокселей ДОЛЖНЫ быть абсолютными внутри сетки!

        _model.RecalculateBoundsPublic();
        _model.SvoDirty = true;
        _renderer.SetEditorModel(_model);
    }

    private void UpdateHover(InputManager input)
    {
        var mousePos = input.GetMousePosition();
        var (ro, rd) = EditorRaycast.ScreenToRay(mousePos, _screenWidth, _screenHeight, _camera);
        bool hovered = EditorRaycast.RaycastModel(ro, rd, _model, out var hoveredVoxel, out _);

        if (hovered)
        {
            var min = new Vector3(hoveredVoxel.X, hoveredVoxel.Y, hoveredVoxel.Z) * Constants.VoxelSize;
            _renderer.SetHoverVoxel(min - new Vector3(0.0001f), min + new Vector3(Constants.VoxelSize + 0.0001f));
        }
        else _renderer.ClearHoverVoxel();
    }

    // Вспомогательные методы камеры и Save/Load такие же как в прошлом ответе
    private void HandleCameraInput(InputManager input)
    {
        var mouseDelta = input.GetRawMouseDelta();
        bool middleDown = input.IsMouseButtonDown(MouseButton.Middle);
        bool shiftDown  = input.IsKeyDown(Keys.LeftShift);
        if (middleDown && shiftDown) _camera.Pan(mouseDelta.X, mouseDelta.Y);
        else if (middleDown)         _camera.Rotate(mouseDelta.X, mouseDelta.Y);
        float scroll = input.GetScrollDelta();
        if (MathF.Abs(scroll) > 0.01f) _camera.Zoom(scroll);
    }

    public void SaveModel(string path) => VoxelModelSerializer.Save(_model, path);
    public void LoadModel(string path)
    {
        var loadedModel = VoxelModelSerializer.Load(path);
        if (loadedModel == null) return;

        _model = loadedModel;

        // 1. Возвращаем привязку к сетке
        float halfExtent = (_gridSize * _voxelSize) / 2.0f;
        _modelWorldPosition = new Vector3(-halfExtent, -halfExtent, -halfExtent);
        _gridCenter = Vector3.Zero;
        _model.LocalCenterOfMass = Vector3.Zero;

        // 2. Центрируем воксели (код центровки из прошлого ответа)
        int maxX = 0, maxY = 0, maxZ = 0;
        foreach (var v in _model.VoxelCoordinates) {
            if (v.X > maxX) maxX = v.X; if (v.Y > maxY) maxY = v.Y; if (v.Z > maxZ) maxZ = v.Z;
        }
        int offsetX = Math.Max(0, (_gridSize - (maxX + 1)) / 2);
        int offsetY = Math.Max(0, (_gridSize - (maxY + 1)) / 2);
        int offsetZ = Math.Max(0, (_gridSize - (maxZ + 1)) / 2);

        var newCoords = new List<Vector3i>();
        var newMats = new Dictionary<Vector3i, uint>();
        foreach (var v in _model.VoxelCoordinates) {
            var newPos = v + new Vector3i(offsetX, offsetY, offsetZ);
            newCoords.Add(newPos);
            newMats[newPos] = _model.VoxelMaterials[v];
        }
        _model.VoxelCoordinates.Clear();
        _model.VoxelCoordinates.AddRange(newCoords);
        _model.VoxelMaterials.Clear();
        foreach (var kvp in newMats) _model.VoxelMaterials[kvp.Key] = kvp.Value;

        // 3. Применяем и ставим камеру
        _model.RecalculateBoundsPublic();
        _model.ForceSetTransform(_modelWorldPosition, Quaternion.Identity);
        _renderer.SetEditorModel(_model);
    
        // 4. Умная камера: подгоняем под размер загруженного объекта
        FitCameraToModel();
    }

    private void FitCameraToModel()
    {
        // 1. Считаем физический размер (диагональ) текущей модели
        float modelDiagonal = (_model.LocalBoundsMax - _model.LocalBoundsMin).Length;

        // 2. Рассчитываем дистанцию:
        // - modelDiagonal * 2.0f: чтобы модель занимала примерно половину экрана по высоте
        // - _voxelSize * 10.0f: минимальная дистанция (чтобы не уткнуться носом в 1 блок)
        float distance = Math.Max(modelDiagonal * 2.0f, _voxelSize * 10.0f);

        // 3. Фокусируемся строго на центре сетки
        _camera.FocusOn(_gridCenter, distance);
    }
    
    public OrbitalCamera Camera      => _camera;
    public VoxelObject   Model       => _model;
    public Tool          ActiveTool  { get => _activeTool;    set => _activeTool = value; }
    public MaterialType  ActiveMaterial { get => _activeMaterial; set => _activeMaterial = value; }
    public int           GridSize    { get => _gridSize; }
    public float         VoxelSize   { get => _voxelSize; }
}