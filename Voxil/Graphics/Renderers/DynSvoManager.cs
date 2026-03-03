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
        public uint          GpuOffset;
        public uint[]?       CurrentData;
        public Task<uint[]>? RebuildTask;
        public bool          PendingRebuild;
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

    public void Update(List<VoxelObject> objects)
    {
        // Сбрасываем список синхронных апгрейдов с прошлого вызова
        _pendingSyncUploads.Clear();

        // ── Фаза 1: Запустить/завершить задачи ──────────────────────────────
        foreach (var obj in objects)
        {
            if (!_slots.TryGetValue(obj, out var slot))
            {
                // Новый объект
                slot = new Slot();
                _slots[obj] = slot;
                obj.SvoDirty = true;
            }

            if (obj.SvoDirty && slot.RebuildTask == null)
            {
                // Нет активной задачи — запускаем сразу
                obj.SvoDirty        = false;
                slot.PendingRebuild = false;
                StartRebuild(obj, slot);
            }
            else if (obj.SvoDirty && slot.RebuildTask != null)
            {
                // Задача уже работает — запомним что нужна ещё одна пересборка
                slot.PendingRebuild = true;
                obj.SvoDirty        = false;
            }

            // Async задача завершена — забираем результат
            if (slot.RebuildTask != null && slot.RebuildTask.IsCompleted)
            {
                if (slot.RebuildTask.IsFaulted)
                {
                    Console.WriteLine($"[DynSvoManager] Rebuild error: " +
                                      $"{slot.RebuildTask.Exception?.GetBaseException().Message}");
                }
                else
                {
                    slot.CurrentData = slot.RebuildTask.Result;
                    UploadSlot(obj, slot, objects);
                }

                slot.RebuildTask = null;

                // Объект изменился пока задача работала — перезапускаем
                if (slot.PendingRebuild)
                {
                    slot.PendingRebuild = false;
                    StartRebuild(obj, slot);
                }
            }
        }

        // ── Фаза 2: Загрузить синхронные апгрейды ───────────────────────────
        // Данные уже готовы (построены синхронно в StartRebuild),
        // здесь только отправляем на GPU
        foreach (var (obj, slot) in _pendingSyncUploads)
        {
            UploadSlot(obj, slot, objects);
        }

        // ── Фаза 3: Удалить слоты мёртвых объектов ──────────────────────────
        // HashSet даёт O(1) Contains вместо O(n) у List → нет O(n²) при 1000 объектах
        var objectSet = new HashSet<VoxelObject>(objects, ReferenceEqualityComparer.Instance);

        List<VoxelObject>? toRemove = null;
        foreach (var key in _slots.Keys)
        {
            if (!objectSet.Contains(key))
                (toRemove ??= new List<VoxelObject>()).Add(key);
        }
        if (toRemove != null)
            foreach (var dead in toRemove)
                _slots.Remove(dead);
    }

    // -------------------------------------------------------------------------
    // Вспомогательные методы
    // -------------------------------------------------------------------------

    private void StartRebuild(VoxelObject obj, Slot slot)
    {
        // Снимаем snapshot на главном треде — фоновый Task не должен трогать VoxelObject
        var  coords     = obj.VoxelCoordinates.ToArray();
        var  matDict    = new Dictionary<Vector3i, uint>(obj.VoxelMaterials);
        uint defaultMat = (uint)obj.Material;
        int  gridSize   = ComputeGridSize(obj);

        obj.SvoGridSize       = gridSize;
        obj.SvoVoxelWorldSize = Constants.VoxelSize; // Scale НЕ включаем — invModel его уберёт

        if (coords.Length <= SYNC_BUILD_THRESHOLD)
        {
            // ── Синхронный путь ──────────────────────────────────────────────
            // SVO готов прямо сейчас, без лага в 1 кадр
            // Выгоден для малых объектов: Task overhead (~0.3ms) > сам билд (~0.01ms)
            slot.CurrentData = SVOBuilder.Build(
                coords,
                pos => matDict.TryGetValue(pos, out var m) ? m : defaultMat,
                gridSize);

            slot.RebuildTask = null;

            // Нельзя вызвать UploadSlot здесь — мы внутри foreach по _slots.
            // Откладываем на фазу 2 в Update()
            _pendingSyncUploads.Add((obj, slot));
        }
        else
        {
            // ── Async путь ───────────────────────────────────────────────────
            // Для больших объектов — не блокируем главный тред
            slot.RebuildTask = Task.Run(() =>
                SVOBuilder.Build(
                    coords,
                    pos => matDict.TryGetValue(pos, out var m) ? m : defaultMat,
                    gridSize));
        }
    }

    private void UploadSlot(VoxelObject obj, Slot slot, List<VoxelObject> allObjects)
    {
        if (slot.CurrentData == null) return;

        uint nodeCount = (uint)(slot.CurrentData.Length / 4);

        // Пул переполнен — перепаковываем
        if (_cursor + nodeCount > MAX_POOL_NODES)
            Compact(allObjects);

        slot.GpuOffset   = _cursor;
        obj.SvoGpuOffset = _cursor;
        _cursor         += nodeCount;

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssbo);
        GL.BufferSubData(
            BufferTarget.ShaderStorageBuffer,
            (IntPtr)((long)slot.GpuOffset * NODE_SIZE_BYTES),
            slot.CurrentData.Length * sizeof(uint),
            slot.CurrentData);
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
