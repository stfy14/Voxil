using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

public class GpuRaycastingRenderer : IDisposable
{
    private Shader _shader;
    private Shader _gridComputeShader;

    private int _quadVao;
    private readonly WorldManager _worldManager;

    // --- GPU Resources ---
    private int _pageTableTexture;      // Статика: Таблица страниц (R32I)
    private int _voxelSsbo;             // Статика: Данные вокселей
    private int _objectGridTexture;     // Динамика: Сетка ускорения (R16I)
    private int _dynamicObjectsBuffer;  // Динамика: Метаданные объектов (SSBO)

    // --- Settings & Constants ---
    private const int ChunkSize = 16;
    private const int ChunkVol = ChunkSize * ChunkSize * ChunkSize;

    // Page Table (Статика)
    private const int PT_X = 512;
    private const int PT_Y = 16;
    private const int PT_Z = 512;
    private const int MASK_X = PT_X - 1;
    private const int MASK_Y = PT_Y - 1;
    private const int MASK_Z = PT_Z - 1;

    // Object Grid (Динамика)
    private const int OBJ_GRID_SIZE = 128; // Размер 3D текстуры сетки
    private const float OBJ_GRID_CELL_SIZE = 4.0f; // Размер одной ячейки в мировых единицах
    private readonly short[] _cpuObjectGrid = new short[OBJ_GRID_SIZE * OBJ_GRID_SIZE * OBJ_GRID_SIZE];

    // --- Memory Management ---
    private int _currentCapacity;
    private readonly int[] _cpuPageTable = new int[PT_X * PT_Y * PT_Z];
    private bool _pageTableDirty = true;

    private Stack<int> _freeSlots = new Stack<int>();
    private Dictionary<Vector3i, int> _loadedChunks = new Dictionary<Vector3i, int>();

    private ConcurrentQueue<Chunk> _chunksToUpload = new ConcurrentQueue<Chunk>();
    private ConcurrentQueue<Vector3i> _chunksToUnload = new ConcurrentQueue<Vector3i>();
    private HashSet<Vector3i> _queuedChunkPositions = new HashSet<Vector3i>();

    private readonly uint[] _chunkUploadBuffer = new uint[ChunkVol];

    // Буфер для данных динамических объектов (CPU копия)
    private GpuDynamicObject[] _tempGpuObjectsArray = new GpuDynamicObject[1024]; // Max 1024 объекта

    private Stopwatch _stopwatch = new Stopwatch();
    private const int PackedChunkSizeInInts = ChunkVol / 4;

    [StructLayout(LayoutKind.Sequential)]
    struct GpuDynamicObject
    {
        public Matrix4 Model; // БЫЛО: InvModel
        public Vector4 Color;
        public Vector4 BoxMin;
        public Vector4 BoxMax;
    }

    public GpuRaycastingRenderer(WorldManager worldManager)
    {
        _worldManager = worldManager;

        // Расчет памяти под чанки
        int diameter = GameSettings.RenderDistance * 2 + 1;
        int estimatedChunks = diameter * diameter * 16;
        _currentCapacity = (int)(estimatedChunks * 1.5f);
        if (_currentCapacity < 10000) _currentCapacity = 10000;

        Console.WriteLine($"[GpuRenderer] Buffer Capacity: {_currentCapacity} chunks");

        for (int i = 0; i < _currentCapacity; i++) _freeSlots.Push(i);
        Array.Fill(_cpuPageTable, -1);
    }

    public void Load()
    {
        // 1. Основной шейдер рейкастинга
        _shader = new Shader("Shaders/raycast.vert", "Shaders/raycast.frag");

        // 2. Compute Shader для обновления сетки объектов
        _gridComputeShader = new Shader("Shaders/grid_update.comp");

        // 3. ИСПРАВЛЕНИЕ: Создаем VAO с dummy VBO
        _quadVao = GL.GenVertexArray();
        GL.BindVertexArray(_quadVao);
        
        // Создаем минимальный VBO (некоторые драйверы требуют хотя бы один buffer)
        int dummyVbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, dummyVbo);
        // 4 вершины * vec2 = 32 байта
        float[] dummyData = new float[8] { -1, -1, 1, -1, -1, 1, 1, 1 };
        GL.BufferData(BufferTarget.ArrayBuffer, sizeof(float) * 8, dummyData, BufferUsageHint.StaticDraw);
        
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        // 4. Page Table (Статика) - 3D текстура для индексов чанков
        _pageTableTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32i,
            PT_X, PT_Y, PT_Z, 0,
            PixelFormat.RedInteger, PixelType.Int, _cpuPageTable);

        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);

        // 5. Voxel SSBO (Статика) - данные вокселей
        _voxelSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsbo);
        long totalBytes = (long)_currentCapacity * PackedChunkSizeInInts * sizeof(uint);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)totalBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);

        // 6. Object Grid (Динамика) - 3D сетка ускорения
        _objectGridTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _objectGridTexture);
        Array.Fill(_cpuObjectGrid, (short)-1);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R16i,
            OBJ_GRID_SIZE, OBJ_GRID_SIZE, OBJ_GRID_SIZE, 0,
            PixelFormat.RedInteger, PixelType.Short, _cpuObjectGrid);

        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameterI(TextureTarget.Texture3D, TextureParameterName.TextureBorderColor, new int[] { -1, -1, -1, -1 });

        // 7. Dynamic Objects SSBO (Динамика) - метаданные объектов
        _dynamicObjectsBuffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer);
        int maxObjectsBytes = _tempGpuObjectsArray.Length * Marshal.SizeOf<GpuDynamicObject>();
        GL.BufferData(BufferTarget.ShaderStorageBuffer, maxObjectsBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);

        Console.WriteLine($"[GpuRenderer] Init complete. Voxel VRAM: {totalBytes / 1024 / 1024} MB. Grid Size: {OBJ_GRID_SIZE}^3");

        // Загружаем начальные чанки
        UploadAllVisibleChunks();
    }

    public void Update(float deltaTime)
    {
        if (PerformanceMonitor.IsEnabled) _stopwatch.Restart();

        // 1. Unload Chunks
        int unloads = 0;
        while (unloads < 100 && _chunksToUnload.TryDequeue(out var pos))
        {
            ProcessUnload(pos);
            unloads++;
        }

        // 2. Upload Chunks
        int uploads = 0;
        int limit = GameSettings.GpuUploadSpeed;
        while (uploads < limit && _chunksToUpload.TryDequeue(out var chunk))
        {
            _queuedChunkPositions.Remove(chunk.Position);
            UploadChunkVoxels(chunk);
            uploads++;
        }

        // 3. Обновляем PageTable на GPU
        if (_pageTableDirty)
        {
            GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, PT_X, PT_Y, PT_Z, PixelFormat.RedInteger, PixelType.Int, _cpuPageTable);
            _pageTableDirty = false;
        }

        // 4. ДИНАМИКА: Обновляем объекты и сетку ускорения (Grid)
        UpdateDynamicObjectsAndGrid();

        if (PerformanceMonitor.IsEnabled)
        {
            _stopwatch.Stop();
            PerformanceMonitor.Record(ThreadType.GpuRender, _stopwatch.ElapsedTicks);
        }
    }

private void UpdateDynamicObjectsAndGrid()
{
    var voxelObjects = _worldManager.GetAllVoxelObjects();
    int count = Math.Min(voxelObjects.Count, _tempGpuObjectsArray.Length);

    // 1. Подготовка данных для SSBO (Матрицы и боксы) - Это быстро, оставляем на CPU
    for (int i = 0; i < count; i++)
    {
        var vo = voxelObjects[i];
        
        // Матрица трансформации (Local -> World)
        // PhysicsWorld сам заботится о центре масс при создании Body, но 
        // VoxelObject.Position - это позиция физического тела.
        // VoxelObject.LocalCenterOfMass - смещение.
        
        // Трансформация: Сначала сдвиг на -CoM (чтобы вращать вокруг центра), потом Вращение, потом Позиция тела
        Matrix4 model = Matrix4.CreateTranslation(-vo.LocalCenterOfMass) * 
                        Matrix4.CreateFromQuaternion(vo.Rotation) * 
                        Matrix4.CreateTranslation(vo.Position);

        var col = MaterialRegistry.GetColor(vo.Material);

        _tempGpuObjectsArray[i] = new GpuDynamicObject
        {
            Model = model,
            Color = new Vector4(col.r, col.g, col.b, 1.0f),
            // Эти Bounds уже включают LocalCenterOfMass? 
            // В VoxelObject: LocalBoundsMin = worldMin - LocalCenterOfMass.
            // Значит это координаты относительно центра масс (физического центра).
            // Нам нужно добавить LocalCenterOfMass обратно, если мы хотим "чистые" локальные координаты вокселей?
            // НЕТ. Матрица 'model' выше уже включает сдвиг на -CoM.
            // Значит, если мы берем вершину (0,0,0) (начало массива вокселей), 
            // умножаем на model -> получаем (0 - CoM) * Rot + Pos. Все верно.
            
            // Но BoxMin/Max в VoxelObject.cs рассчитаны как "абсолютные" минус CoM.
            // Значит, чтобы получить AABB в "пространстве вокселей" (где воксели от 0 до Size), 
            // нам нужно прибавить CoM.
            BoxMin = new Vector4(vo.LocalBoundsMin + vo.LocalCenterOfMass, 0),
            BoxMax = new Vector4(vo.LocalBoundsMax + vo.LocalCenterOfMass, 0)
        };
    }

    // 2. Заливка данных объектов в SSBO
    if (count > 0)
    {
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, count * Marshal.SizeOf<GpuDynamicObject>(), _tempGpuObjectsArray);
    }

    // 3. COMPUTE SHADER: Обновление сетки
    // А. Расчет параметров сетки
    float snap = OBJ_GRID_CELL_SIZE;
    Vector3 snappedCenter = new Vector3(
        (float)Math.Floor(_worldManager.GetPlayerPosition().X / snap) * snap,
        (float)Math.Floor(_worldManager.GetPlayerPosition().Y / snap) * snap,
        (float)Math.Floor(_worldManager.GetPlayerPosition().Z / snap) * snap
    );
    float halfExtent = (OBJ_GRID_SIZE * OBJ_GRID_CELL_SIZE) / 2.0f;
    Vector3 gridOrigin = snappedCenter - new Vector3(halfExtent);
    _lastGridOrigin = gridOrigin;

    // Б. Очистка текстуры сетки (-1)
    // ClearTexImage очищает значением. Нам нужно -1 (0xFFFF для short).
    // OpenGL ожидает массив.
    int clearVal = -1; 
    GL.ClearTexImage(_objectGridTexture, 0, PixelFormat.RedInteger, PixelType.Short, new int[] { clearVal });

    // В. Запуск шейдера
    if (count > 0)
    {
        _gridComputeShader.Use();
        _gridComputeShader.SetInt("uObjectCount", count);
        _gridComputeShader.SetVector3("uGridOrigin", gridOrigin);
        _gridComputeShader.SetFloat("uGridStep", OBJ_GRID_CELL_SIZE);
        _gridComputeShader.SetInt("uGridSize", OBJ_GRID_SIZE);
        
        // Биндим текстуру для записи (Image Load/Store)
        GL.BindImageTexture(0, _objectGridTexture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R16i);
        
        // Биндим SSBO объектов (binding = 2)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);

        // Считаем кол-во групп
        int groups = (count + 63) / 64; 
        GL.DispatchCompute(groups, 1, 1);

        // Г. Барьер памяти
        // Гарантируем, что Compute Shader закончил писать в текстуру до того, как Fragment Shader начнет её читать
        GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.ShaderImageAccessBarrierBit);
    }
}

    private Vector3 _lastGridOrigin;

    public void Render(Camera camera)
    {
        if (PerformanceMonitor.IsEnabled) _stopwatch.Restart();

        _shader.Use();

        // Камера
        _shader.SetVector3("uCamPos", camera.Position);
        _shader.SetMatrix4("uView", camera.GetViewMatrix());
        _shader.SetMatrix4("uProjection", camera.GetProjectionMatrix());
        _shader.SetFloat("uRenderDistance", _worldManager.GetViewRangeInVoxels());
        _shader.SetVector3("uSunDir", Vector3.Normalize(new Vector3(0.5f, 0.8f, 0.2f)));

        // --- ДОБАВЬ ЭТОТ БЛОК ---
        // Вычисляем и передаем границы загруженной зоны для статики
        Vector3 camPos = camera.Position;
        int camCx = (int)Math.Floor(camPos.X / ChunkSize);
        int camCy = (int)Math.Floor(camPos.Y / ChunkSize);
        int camCz = (int)Math.Floor(camPos.Z / ChunkSize);

        int range = _worldManager.GetViewRangeInVoxels() / ChunkSize;

        _shader.SetInt("uBoundMinX", camCx - range);
        _shader.SetInt("uBoundMinY", 0); // Мир не бесконечен по высоте
        _shader.SetInt("uBoundMinZ", camCz - range);

        // Верхняя граница эксклюзивная (шейдер использует greaterThanEqual)
        _shader.SetInt("uBoundMaxX", camCx + range + 1);
        _shader.SetInt("uBoundMaxY", WorldManager.WorldHeightChunks);
        _shader.SetInt("uBoundMaxZ", camCz + range + 1);
        // ------------------------

        // Статика (Page Table)
        GL.BindImageTexture(0, _pageTableTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32i);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);

        // Динамика (Object Grid)
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture3D, _objectGridTexture);
        _shader.SetInt("uObjectGrid", 1);

        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);

        // Grid Uniforms
        _shader.SetVector3("uGridOrigin", _lastGridOrigin);
        _shader.SetFloat("uGridStep", OBJ_GRID_CELL_SIZE);
        _shader.SetInt("uGridSize", OBJ_GRID_SIZE);

        int dynObjectCount = Math.Min(_worldManager.GetAllVoxelObjects().Count, _tempGpuObjectsArray.Length);
        _shader.SetInt("uObjectCount", dynObjectCount);

        // Рисуем Fullscreen Quad
        GL.BindVertexArray(_quadVao);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        if (PerformanceMonitor.IsEnabled)
        {
            _stopwatch.Stop();
            PerformanceMonitor.Record(ThreadType.GpuRender, _stopwatch.ElapsedTicks);
        }
    }

    // --- Helpers (Internal) ---

    private int GetPageTableIndex(Vector3i chunkPos)
    {
        int tx = chunkPos.X & MASK_X;
        int ty = chunkPos.Y & MASK_Y;
        int tz = chunkPos.Z & MASK_Z;
        return tx + PT_X * (ty + PT_Y * tz);
    }

    private void ProcessUnload(Vector3i pos)
    {
        if (_loadedChunks.TryGetValue(pos, out int offset))
        {
            _loadedChunks.Remove(pos);
            _freeSlots.Push(offset);
            int index = GetPageTableIndex(pos);
            _cpuPageTable[index] = -1;
            _pageTableDirty = true;
        }
    }

    private void UploadChunkVoxels(Chunk chunk)
    {
        if (chunk == null || !chunk.IsLoaded) return;

        int offset;
        if (_loadedChunks.TryGetValue(chunk.Position, out int existingOffset)) offset = existingOffset;
        else
        {
            if (_freeSlots.Count == 0) ResizeBuffer();
            offset = _freeSlots.Pop();
            _loadedChunks[chunk.Position] = offset;
        }

        // Bit-packing 4 bytes -> 1 uint
        var src = chunk.GetVoxelsUnsafe();
        for (int i = 0; i < PackedChunkSizeInInts; i++)
        {
            int baseIdx = i * 4;
            uint packed = (uint)(src[baseIdx] | (src[baseIdx + 1] << 8) | (src[baseIdx + 2] << 16) | (src[baseIdx + 3] << 24));
            _chunkUploadBuffer[i] = packed;
        }

        IntPtr gpuOffset = (IntPtr)((long)offset * PackedChunkSizeInInts * sizeof(uint));
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsbo);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, gpuOffset, PackedChunkSizeInInts * sizeof(uint), _chunkUploadBuffer);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

        _cpuPageTable[GetPageTableIndex(chunk.Position)] = offset * PackedChunkSizeInInts;
        _pageTableDirty = true;
    }

    private void ResizeBuffer()
    {
        int newCapacity = _currentCapacity * 2;
        long oldBytes = (long)_currentCapacity * PackedChunkSizeInInts * sizeof(uint);
        long newBytes = (long)newCapacity * PackedChunkSizeInInts * sizeof(uint);

        Console.WriteLine($"[GpuRenderer] Resizing SSBO: {_currentCapacity}->{newCapacity} ({newBytes / 1024 / 1024} MB)");

        int newSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, newSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)newBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        GL.CopyNamedBufferSubData(_voxelSsbo, newSsbo, IntPtr.Zero, IntPtr.Zero, (IntPtr)oldBytes);

        GL.DeleteBuffer(_voxelSsbo);
        _voxelSsbo = newSsbo;
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);

        for (int i = _currentCapacity; i < newCapacity; i++) _freeSlots.Push(i);
        _currentCapacity = newCapacity;
    }

    // --- External Interface ---

    public void UploadAllVisibleChunks()
    {
        var chunks = _worldManager.GetChunksSnapshot();
        foreach (var c in chunks) NotifyChunkLoaded(c);
    }

    public void NotifyChunkLoaded(Chunk chunk)
    {
        if (chunk != null && chunk.IsLoaded)
        {
            if (_queuedChunkPositions.Add(chunk.Position)) _chunksToUpload.Enqueue(chunk);
        }
    }

    public void UnloadChunk(Vector3i chunkPos) => _chunksToUnload.Enqueue(chunkPos);

    public void OnResize(int width, int height) => GL.Viewport(0, 0, width, height);

    public void Dispose()
    {
        GL.DeleteTexture(_pageTableTexture);
        GL.DeleteTexture(_objectGridTexture);
        GL.DeleteBuffer(_voxelSsbo);
        GL.DeleteBuffer(_dynamicObjectsBuffer);
        GL.DeleteVertexArray(_quadVao);
        _shader?.Dispose();
    }
}