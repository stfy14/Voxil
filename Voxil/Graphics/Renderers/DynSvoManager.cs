// --- DynSvoManager.cs ---
// Управляет единым GPU-пулом SVO-данных для всех динамических VoxelObject.
//
// Архитектура:
//   - Один SSBO (binding = 7) — пул из MAX_POOL_NODES узлов (32 MB)
//   - При изменении объекта (SvoDirty=true): запускается Task.Run → async rebuild
//   - Когда Task завершён: данные загружаются на GPU через BufferSubData
//   - Пока Task работает: GPU продолжает рендерить старый SVO (лаг 1 кадр)
//   - При нехватке пула: полная перепаковка (compaction) всех активных объектов
//
// Обновление позиций в GpuDynamicObject:
//   FillGpuObject в GpuRaycastingRenderer читает obj.SvoGpuOffset каждый кадр,
//   поэтому смена оффсета во время compaction подхватывается автоматически.

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
    // Типичный игровой объект 64³ при 20% заполнении: ~30 000 узлов ≈ 480 KB
    // → пул вмещает ~65 таких объектов одновременно
    private const int  MAX_POOL_NODES  = 2_000_000;
    private const int  NODE_SIZE_BYTES = 16; // uvec4

    // -------------------------------------------------------------------------
    // Состояние
    // -------------------------------------------------------------------------

    private int  _ssbo;
    private bool _disposed;

    // Курсор последовательного аллокатора (в узлах, не байтах)
    private uint _cursor;

    // Информация об одном объекте в пуле
    private sealed class Slot
    {
        public uint     GpuOffset;    // смещение в пуле (в узлах)
        public uint[]?  CurrentData;  // последние загруженные данные
        public Task<uint[]>? RebuildTask; // работающий async rebuild, null = простой
        // Если объект стал dirty пока задача уже работала — запустим ещё раз после
        public bool     PendingRebuild;
    }

    // Используем ReferenceEquality — объекты идентифицируются по ссылке, не по значению
    private readonly Dictionary<VoxelObject, Slot> _slots =
        new(ReferenceEqualityComparer.Instance);

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
    // Обновление — вызывать каждый кадр из GpuRaycastingRenderer.Update()
    // -------------------------------------------------------------------------

    public void Update(List<VoxelObject> objects)
    {
        // --- Фаза 1: Запустить/завершить async задачи ---

        foreach (var obj in objects)
        {
            if (!_slots.TryGetValue(obj, out var slot))
            {
                // Новый объект — создаём слот и сразу помечаем для сборки
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
                // Задача уже работает — запомним что нужна ещё одна пересборка
                slot.PendingRebuild = true;
                obj.SvoDirty = false;
            }

            // Задача завершена — забираем результат
            if (slot.RebuildTask != null && slot.RebuildTask.IsCompleted)
            {
                if (slot.RebuildTask.IsFaulted)
                {
                    Console.WriteLine($"[DynSvoManager] Rebuild error for object: " +
                                      $"{slot.RebuildTask.Exception?.GetBaseException().Message}");
                }
                else
                {
                    slot.CurrentData = slot.RebuildTask.Result;
                    UploadSlot(obj, slot, objects);
                }

                slot.RebuildTask = null;

                // Если за время работы задачи объект снова изменился — перезапускаем
                if (slot.PendingRebuild)
                {
                    slot.PendingRebuild = false;
                    StartRebuild(obj, slot);
                }
            }
        }

        // --- Фаза 2: Удалить слоты мёртвых объектов ---
        // Собираем ключи для удаления (нельзя изменять словарь в foreach)
        List<VoxelObject>? toRemove = null;
        foreach (var key in _slots.Keys)
        {
            if (!objects.Contains(key))
                (toRemove ??= new List<VoxelObject>()).Add(key);
        }
        if (toRemove != null)
            foreach (var dead in toRemove) _slots.Remove(dead);
    }

    // -------------------------------------------------------------------------
    // Вспомогательные методы
    // -------------------------------------------------------------------------

    private void StartRebuild(VoxelObject obj, Slot slot)
    {
        // Снимаем snapshot на главном треде — Task.Run не должен трогать VoxelObject
        var coords      = obj.VoxelCoordinates.ToArray(); // иммутабельная копия
        var matDict     = new Dictionary<Vector3i, uint>(obj.VoxelMaterials); // копия
        uint defaultMat = (uint)obj.Material;
        int gridSize    = ComputeGridSize(obj);

        // Сохраняем в объект чтобы GpuDynamicObject мог читать правильный gridSize
        obj.SvoGridSize       = gridSize;
        obj.SvoVoxelWorldSize = Constants.VoxelSize;

        slot.RebuildTask = Task.Run(() =>
            SVOBuilder.Build(
                coords,
                pos => matDict.TryGetValue(pos, out var m) ? m : defaultMat,
                gridSize));
    }

    private void UploadSlot(VoxelObject obj, Slot slot, List<VoxelObject> allObjects)
    {
        if (slot.CurrentData == null) return;

        uint nodeCount = (uint)(slot.CurrentData.Length / 4);

        // Пытаемся выделить место в пуле
        if (_cursor + nodeCount > MAX_POOL_NODES)
        {
            // Пул переполнен — перепаковываем все активные объекты
            Compact(allObjects);
        }

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
    /// Сбрасывает курсор и перепаковывает все активные объекты в начало пула.
    /// Вызывается только при переполнении — нечасто.
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

            uint nodeCount = (uint)(slot.CurrentData.Length / 4);

            slot.GpuOffset   = _cursor;
            obj.SvoGpuOffset = _cursor;

            GL.BufferSubData(
                BufferTarget.ShaderStorageBuffer,
                (IntPtr)((long)_cursor * NODE_SIZE_BYTES),
                slot.CurrentData.Length * sizeof(uint),
                slot.CurrentData);

            _cursor += nodeCount;
        }

        Console.WriteLine($"[DynSvoManager] After compaction: {_cursor}/{MAX_POOL_NODES} nodes used.");
    }

    /// <summary>
    /// Вычисляет размер сетки SVO для объекта (ближайшая степень двойки >= max координата).
    /// После RebuildMeshAndPhysics координаты нормализованы к [0, maxCoord].
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
