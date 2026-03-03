// --- DynSvoManager.cs ---
// Управляет единым GPU-пулом SVO-данных для всех динамических VoxelObject.
//
// Оптимизации:
//   - Синхронный билд для малых объектов (≤256 вокселей) — результат в том же кадре
//   - HashSet для O(1) проверки мёртвых слотов вместо O(n) List.Contains
//   - Async билд для больших объектов — не блокирует главный тред

using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public sealed class DynSvoManager : IDisposable
{
    // -------------------------------------------------------------------------
    // Константы
    // -------------------------------------------------------------------------

    public const int BindingSlot = 7;

    // 32 MB = 2 000 000 узлов × 16 байт
    private const int  MAX_POOL_NODES  = 2_000_000;
    private const int  NODE_SIZE_BYTES = 16;

    // Объекты с количеством вокселей <= порога строятся синхронно (без Task.Run),
    // чтобы SVO был готов в том же кадре где был создан объект.
    // TNT = 13 вокселей — попадает сюда.
    private const int  SYNC_BUILD_THRESHOLD = 256;

    // -------------------------------------------------------------------------
    // GPU буфер
    // -------------------------------------------------------------------------

    private int  _ssbo;
    private bool _disposed;

    // Курсор последовательного аллокатора (в узлах, не байтах)
    private uint _cursor;

    // -------------------------------------------------------------------------
    // Слот одного объекта
    // -------------------------------------------------------------------------

    private sealed class Slot
    {
        public uint GpuOffset;
        public uint[] CurrentData;
        public Task<uint[]> RebuildTask;
        public bool PendingRebuild;
        public int BuiltGridSize; // ← новое
    }

    private readonly Dictionary<VoxelObject, Slot> _slots =
        new(ReferenceEqualityComparer.Instance);

    // Синхронные апгрейды текущего кадра
    // Заполняется в StartRebuild, обрабатывается в Update после foreach
    private readonly List<(VoxelObject obj, Slot slot)> _pendingSyncUploads = new();

    // -------------------------------------------------------------------------
    // Инициализация / Dispose
    // -------------------------------------------------------------------------

    public void Initialize()
    {
        _ssbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssbo);
        GL.BufferData(
            BufferTarget.ShaderStorageBuffer,
            (IntPtr)((long)MAX_POOL_NODES * NODE_SIZE_BYTES),
            IntPtr.Zero,
            BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BindingSlot, _ssbo);
    }

    public void Bind() =>
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BindingSlot, _ssbo);

    // -------------------------------------------------------------------------
    // Update — вызывать каждый кадр ДО UpdateDynamicObjectsAndGrid(),
    // чтобы FillGpuObject читал уже актуальные SvoGpuOffset
    // -------------------------------------------------------------------------

public void Update(List<VoxelObject> objects, VoxelObject viewModel)
    {
        _pendingSyncUploads.Clear();

        // 1. Обрабатываем обычные объекты
        for (int i = 0; i < objects.Count; i++)
        {
            ProcessSingleObject(objects[i], objects);
        }

        // 1.5. Обрабатываем ViewModel (Динамит в руке)
        if (viewModel != null)
        {
            ProcessSingleObject(viewModel, objects);
        }

        // 2. Загружаем быстрые обновления
        for (int i = 0; i < _pendingSyncUploads.Count; i++)
        {
            UploadSlot(_pendingSyncUploads[i].obj, _pendingSyncUploads[i].slot, objects);
        }

        // 3. Очищаем удаленные объекты
        List<VoxelObject> toRemove = null;
        foreach (var key in _slots.Keys)
        {
            // ВАЖНО: Не удаляем viewModel из памяти!
            if (key != viewModel && !objects.Contains(key)) 
            {
                if (toRemove == null) toRemove = new List<VoxelObject>();
                toRemove.Add(key);
            }
        }
        
        if (toRemove != null)
        {
            for (int i = 0; i < toRemove.Count; i++) _slots.Remove(toRemove[i]);
        }
    }

    // Вынесли логику в отдельный метод, чтобы код был чистым
    private void ProcessSingleObject(VoxelObject obj, List<VoxelObject> allObjects)
    {
        if (!_slots.TryGetValue(obj, out var slot))
        {
            slot = new Slot();
            _slots[obj] = slot;
            obj.SvoDirty = true;
        }

        if (obj.SvoDirty && slot.RebuildTask == null)
        {
            obj.SvoDirty = false;
            slot.PendingRebuild = false;
            StartRebuild(obj, slot);
        }
        else if (obj.SvoDirty && slot.RebuildTask != null)
        {
            slot.PendingRebuild = true;
            obj.SvoDirty = false;
        }

        if (slot.RebuildTask != null && slot.RebuildTask.IsCompleted)
        {
            if (!slot.RebuildTask.IsFaulted)
            {
                slot.CurrentData = slot.RebuildTask.Result;
                UploadSlot(obj, slot, allObjects);
            }
            slot.RebuildTask = null;

            if (slot.PendingRebuild)
            {
                slot.PendingRebuild = false;
                StartRebuild(obj, slot);
            }
        }
    }
    
    private void StartRebuild(VoxelObject obj, Slot slot)
    {
        var coords      = obj.VoxelCoordinates.ToArray();
        var matDict     = new Dictionary<Vector3i, uint>(obj.VoxelMaterials);
        uint defaultMat = (uint)obj.Material;
        int gridSize    = ComputeGridSize(obj);

        slot.BuiltGridSize    = gridSize;
        obj.SvoGridSize       = 0;
        obj.SvoVoxelWorldSize = 0.0f;

        Func<Vector3i, uint> getMaterial = pos =>
            matDict.TryGetValue(pos, out var m) ? m : defaultMat;

        if (coords.Length <= SYNC_BUILD_THRESHOLD)
        {
            slot.CurrentData  = SVOBuilder.Build(coords, getMaterial, gridSize);
            slot.RebuildTask  = null;
            _pendingSyncUploads.Add((obj, slot));
        }
        else
        {
            slot.RebuildTask = Task.Run(() =>
                SVOBuilder.Build(coords, getMaterial, gridSize));
        }
    }

    private void UploadSlot(VoxelObject obj, Slot slot, List<VoxelObject> allObjects)
    {
        if (slot.CurrentData == null || slot.CurrentData.Length == 0) return;

        uint nodeCount = (uint)(slot.CurrentData.Length / 4);
        if (nodeCount == 0) return;

        if (_cursor + nodeCount > MAX_POOL_NODES)
            Compact(allObjects);

        slot.GpuOffset   = _cursor;
        obj.SvoGpuOffset = _cursor;

        _cursor += nodeCount;

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssbo);
        GL.BufferSubData(
            BufferTarget.ShaderStorageBuffer,
            (IntPtr)((long)slot.GpuOffset * NODE_SIZE_BYTES),
            slot.CurrentData.Length * sizeof(uint),
            slot.CurrentData);

        obj.SvoGridSize       = slot.BuiltGridSize;
        obj.SvoVoxelWorldSize = Constants.VoxelSize;
    }

    /// <summary>
    /// Перепаковывает все активные объекты в начало пула.
    /// Вызывается только при переполнении.
    /// </summary>
    private void Compact(List<VoxelObject> objects)
    {
        Console.WriteLine("[DynSvoManager] Compacting SVO pool...");
        _cursor = 0;

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssbo);

        foreach (var obj in objects)
        {
            if (!_slots.TryGetValue(obj, out var slot)) continue;
            if (slot.CurrentData == null) continue;

            uint nodeCount   = (uint)(slot.CurrentData.Length / 4);
            slot.GpuOffset   = _cursor;
            obj.SvoGpuOffset = _cursor;

            GL.BufferSubData(
                BufferTarget.ShaderStorageBuffer,
                (IntPtr)((long)_cursor * NODE_SIZE_BYTES),
                slot.CurrentData.Length * sizeof(uint),
                slot.CurrentData);

            _cursor += nodeCount;
        }

        Console.WriteLine($"[DynSvoManager] After compaction: {_cursor}/{MAX_POOL_NODES} nodes.");
    }

    /// <summary>
    /// Ближайшая степень двойки >= максимальной координате объекта.
    /// После нормализации координат в VoxelObject они начинаются с 0.
    /// </summary>
    public static int ComputeGridSize(VoxelObject obj)
    {
        if (obj.VoxelCoordinates.Count == 0) return 1;

        int maxCoord = 0;
        foreach (var v in obj.VoxelCoordinates)
        {
            if (v.X + 1 > maxCoord) maxCoord = v.X + 1;
            if (v.Y + 1 > maxCoord) maxCoord = v.Y + 1;
            if (v.Z + 1 > maxCoord) maxCoord = v.Z + 1;
        }

        return SVOBuilder.NextPowerOfTwo(Math.Max(maxCoord, 1));
    }

    // -------------------------------------------------------------------------
    // Dispose
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ssbo != 0)
        {
            GL.DeleteBuffer(_ssbo);
            _ssbo = 0;
        }
    }
}
