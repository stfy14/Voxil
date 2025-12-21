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
    private int _quadVao;
    private readonly WorldManager _worldManager;

    // --- GPU Resources ---
    private int _pageTableTexture;
    private int _voxelSsbo;
    private int _dynamicObjectsBuffer;

    // --- Settings & Constants ---
    private const int ChunkSize = 16;
    private const int ChunkVol = ChunkSize * ChunkSize * ChunkSize;
    
    // Размер таблицы страниц (СТЕПЕНИ ДВОЙКИ!)
    // 128x16x128 позволяет держать активную зону 2048x256x2048 блоков.
    private const int PT_X = 128;
    private const int PT_Y = 16;
    private const int PT_Z = 128;
    
    // Битовые маски для быстрого взятия остатка (аналог % 128)
    private const int MASK_X = PT_X - 1;
    private const int MASK_Y = PT_Y - 1;
    private const int MASK_Z = PT_Z - 1;

    // --- Memory Management ---
    private int _currentCapacity; 
    
    // CPU копия таблицы для быстрой заливки в текстуру
    private readonly int[] _cpuPageTable = new int[PT_X * PT_Y * PT_Z];
    private bool _pageTableDirty = true; // Флаг: были изменения, нужно обновить текстуру

    // Пул свободных слотов в SSBO
    private Stack<int> _freeSlots = new Stack<int>();
    
    // Отслеживание загруженных чанков: Position -> Offset in SSBO
    private Dictionary<Vector3i, int> _loadedChunks = new Dictionary<Vector3i, int>();
    
    // Очереди задач
    private ConcurrentQueue<Chunk> _chunksToUpload = new ConcurrentQueue<Chunk>();
    private ConcurrentQueue<Vector3i> _chunksToUnload = new ConcurrentQueue<Vector3i>();
    private HashSet<Vector3i> _queuedChunkPositions = new HashSet<Vector3i>();
    
    // Временные буферы (Zero-Allocation)
    private readonly uint[] _chunkUploadBuffer = new uint[ChunkVol];
    private GpuDynamicObject[] _tempGpuObjectsArray = new GpuDynamicObject[256];
    private Stopwatch _stopwatch = new Stopwatch();
    private const int PackedChunkSizeInInts = ChunkVol / 4; 

    [StructLayout(LayoutKind.Sequential)]
    struct GpuDynamicObject {
        public Matrix4 InvModel;
        public Vector4 Color;
        public Vector4 BoxMin;
        public Vector4 BoxMax;
    }

    public GpuRaycastingRenderer(WorldManager worldManager)
    {
        _worldManager = worldManager;

        // --- ОПТИМИЗАЦИЯ 1: Расчет VRAM под настройки ---
        // Считаем объем видимой области: (RenderDistance * 2 + 1)^2 * высота
        int diameter = GameSettings.RenderDistance * 2 + 1;
        // 16 - это WorldManager.WorldHeightChunks, лучше брать константу, но пока хардкод для надежности
        int estimatedChunks = diameter * diameter * 16; 
        
        // Даем запас 50% на случай резких движений или загрузки краев
        _currentCapacity = (int)(estimatedChunks * 1.5f);
        
        // Минимальный порог безопасности (например, 10000 чанков ~40МБ)
        if (_currentCapacity < 10000) _currentCapacity = 10000;
        
        Console.WriteLine($"[GpuRenderer] Buffer Capacity set to: {_currentCapacity} chunks");

        // Заполняем пул свободных слотов ПРОСТЫМИ ИНДЕКСАМИ (исправление из прошлого ответа)
        for (int i = 0; i < _currentCapacity; i++)
            _freeSlots.Push(i);
            
        // Инициализируем таблицу пустотой (-1)
        Array.Fill(_cpuPageTable, -1);
    }

    public void Load()
    {
        _shader = new Shader("Shaders/raycast.vert", "Shaders/raycast.frag");
        _quadVao = GL.GenVertexArray();

        // 1. Page Table Texture (3D Texture, R32I)
        _pageTableTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
        // Заливаем начальное состояние (-1)
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32i, 
            PT_X, PT_Y, PT_Z, 0, 
            PixelFormat.RedInteger, PixelType.Int, _cpuPageTable);
        
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        
        // ClampToEdge, так как мы реализуем цикличность через битовые маски координат
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        
        // Привязываем к Image Unit 0 для шейдера
        GL.BindImageTexture(0, _pageTableTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32i);

        // 2. SSBO (Voxel Data)
        _voxelSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsbo);
        long totalBytes = (long)_currentCapacity * PackedChunkSizeInInts * sizeof(uint);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)totalBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);
        
        // 3. Dynamic Objects SSBO
        _dynamicObjectsBuffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, _tempGpuObjectsArray.Length * Marshal.SizeOf<GpuDynamicObject>(), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);

        Console.WriteLine($"[GpuRenderer] Allocated VRAM: {totalBytes / 1024 / 1024} MB for voxels.");
        
        // Загружаем то, что уже успело сгенерироваться
        UploadAllVisibleChunks();
    }

    public void Update(float deltaTime)
    {
        if (PerformanceMonitor.IsEnabled) _stopwatch.Restart();

        // 1. Unload
        int unloads = 0;
        while (unloads < 100 && _chunksToUnload.TryDequeue(out var pos))
        {
            ProcessUnload(pos);
            unloads++;
        }

        // 2. Upload (Используем GameSettings)
        int uploads = 0;
        int limit = GameSettings.GpuUploadSpeed; // <-- БЕРЕМ ИЗ НАСТРОЕК

        while (uploads < limit && _chunksToUpload.TryDequeue(out var chunk))
        {
            _queuedChunkPositions.Remove(chunk.Position);
            UploadChunkVoxels(chunk);
            uploads++;
        }
        
        // 3. Обновляем динамические объекты (игроки, предметы)
        UpdateDynamicObjects();

        // 4. Синхронизируем таблицу страниц с GPU (если были изменения)
        if (_pageTableDirty)
        {
            GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
            // Заливаем весь массив int[] разом. Это быстро (~0.5мс для 256KB данных).
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0,
                PT_X, PT_Y, PT_Z,
                PixelFormat.RedInteger, PixelType.Int, _cpuPageTable);
            _pageTableDirty = false;
        }

        if (PerformanceMonitor.IsEnabled)
        {
            _stopwatch.Stop();
            PerformanceMonitor.Record(ThreadType.GpuRender, _stopwatch.ElapsedTicks);
        }
    }

    // --- Helper: Toroidal Indexing ---
    // Преобразует мировые координаты чанка в индекс массива таблицы страниц.
    // Использует побитовое И (&) для взятия остатка по степеням двойки.
    private int GetPageTableIndex(Vector3i chunkPos)
    {
        int tx = chunkPos.X & MASK_X;
        int ty = chunkPos.Y & MASK_Y;
        int tz = chunkPos.Z & MASK_Z;

        // Линейный индекс: x + y*Width + z*Width*Height
        // Порядок координат должен совпадать с тем, как OpenGL заполняет 3D текстуру.
        // Обычно это X (row), затем Y (slice), затем Z (depth) ??? 
        // В OpenGL TexImage3D data order: [depth][height][width] -> z, y, x
        // Значит индекс = x + PT_X * (y + PT_Y * z)
        return tx + PT_X * (ty + PT_Y * tz);
    }

    private void ProcessUnload(Vector3i pos)
    {
        if (_loadedChunks.TryGetValue(pos, out int offset))
        {
            // 1. Возвращаем слот в пул
            _loadedChunks.Remove(pos);
            _freeSlots.Push(offset);
            
            // 2. Обновляем CPU таблицу страниц
            int index = GetPageTableIndex(pos);
            _cpuPageTable[index] = -1; // -1 означает "пусто"
            _pageTableDirty = true;
        }
    }

    private void UploadChunkVoxels(Chunk chunk)
    {
        if (chunk == null || !chunk.IsLoaded) return;
        
        int offset;
        // Проверяем, может чанк уже загружен (перезаливка)
        if (_loadedChunks.TryGetValue(chunk.Position, out int existingOffset))
        {
            offset = existingOffset;
        }
        else
        {
            if (_freeSlots.Count == 0)
            {
                ResizeBuffer(); // Критический случай
            }
            offset = _freeSlots.Pop();
            _loadedChunks[chunk.Position] = offset;
        }

        // 1. Копируем воксели в промежуточный буфер (Zero-Allocation)
        var src = chunk.GetVoxelsUnsafe();;
        for(int i = 0; i < PackedChunkSizeInInts; i++) 
        {
            int baseIdx = i * 4;
            // Little Endian packing:
            // Byte 0 -> Bits 0-7
            // Byte 1 -> Bits 8-15
            // ...
            uint packed = (uint)(src[baseIdx] | 
                                 (src[baseIdx + 1] << 8) | 
                                 (src[baseIdx + 2] << 16) | 
                                 (src[baseIdx + 3] << 24));
            
            _chunkUploadBuffer[i] = packed;
        }

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsbo);
        // ВАЖНО: Offset умножаем на PackedChunkSizeInInts, а не ChunkVol
        // Offset в словаре _loadedChunks теперь означает "номер слота", а не "индекс вокселя"
        // Один слот = 1024 uint-а (для 16^3 чанка)
        
        IntPtr gpuOffset = (IntPtr)((long)offset * PackedChunkSizeInInts * sizeof(uint));
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, gpuOffset, PackedChunkSizeInInts * sizeof(uint), _chunkUploadBuffer);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

        int index = GetPageTableIndex(chunk.Position);
        
        // ВАЖНО: В таблицу страниц мы пишем не байтовый оффсет, а ИНДЕКС UINT-а в массиве SSBO.
        // То есть: номер слота * размер слота в интах.
        _cpuPageTable[index] = offset * PackedChunkSizeInInts; 
        
        _pageTableDirty = true;
    }

    public void Render(Camera camera)
    {
        if (PerformanceMonitor.IsEnabled) _stopwatch.Restart();
        
        _shader.Use();

        _shader.SetVector3("uCamPos", camera.Position);
        _shader.SetMatrix4("uView", camera.GetViewMatrix());
        _shader.SetMatrix4("uProjection", camera.GetProjectionMatrix());
        
        Vector3 camPos = camera.Position;
        // Текущий чанк, в котором находится камера
        int camCx = (int)Math.Floor(camPos.X / ChunkSize);
        int camCy = (int)Math.Floor(camPos.Y / ChunkSize);
        int camCz = (int)Math.Floor(camPos.Z / ChunkSize);
        
        // Вычисляем границы для Ray Box Intersection в шейдере.
        // Это ограничивает длину луча только загруженной областью.
        int range = _worldManager.GetViewRangeInVoxels() / ChunkSize;
        
        // Min - включительно
        _shader.SetInt("uBoundMinX", camCx - range);
        _shader.SetInt("uBoundMinY", 0); 
        _shader.SetInt("uBoundMinZ", camCz - range);
        
        // Max - ЭКСКЛЮЗИВНО (+1)
        // Если чанк 15 - последний валидный, мы передаем 16. Шейдер остановится, когда дойдет до 16.
        _shader.SetInt("uBoundMaxX", camCx + range + 1);
        _shader.SetInt("uBoundMaxY", 16); 
        _shader.SetInt("uBoundMaxZ", camCz + range + 1);

        _shader.SetFloat("uRenderDistance", _worldManager.GetViewRangeInVoxels()); 

        // Биндим ресурсы
        GL.BindImageTexture(0, _pageTableTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32i);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);

        int dynCount = Math.Min(_worldManager.GetAllVoxelObjects().Count, _tempGpuObjectsArray.Length);
        _shader.SetInt("uDynamicObjectCount", dynCount);

        // Рисуем полноэкранный квад
        GL.BindVertexArray(_quadVao);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        if (PerformanceMonitor.IsEnabled)
        {
            _stopwatch.Stop();
            PerformanceMonitor.Record(ThreadType.GpuRender, _stopwatch.ElapsedTicks);
        }
    }

    // Метод разрушения одного вокселя (O(1))
    /*public void DestroyVoxelFast(Vector3i worldPos) 
    {
        int cx = (int)Math.Floor(worldPos.X / (float)ChunkSize);
        int cy = (int)Math.Floor(worldPos.Y / (float)ChunkSize);
        int cz = (int)Math.Floor(worldPos.Z / (float)ChunkSize);
        Vector3i cPos = new Vector3i(cx, cy, cz);

        if (_loadedChunks.TryGetValue(cPos, out int offset))
        {
            int lx = worldPos.X - cx * ChunkSize;
            int ly = worldPos.Y - cy * ChunkSize;
            int lz = worldPos.Z - cz * ChunkSize;
            
            if (lx >=0 && lx < ChunkSize && ly >=0 && ly < ChunkSize && lz >=0 && lz < ChunkSize)
            {
                int voxelIndex = lx + ChunkSize * (ly + ChunkSize * lz);
                uint air = 0;
                IntPtr gpuOffset = (IntPtr)(((long)offset + voxelIndex) * sizeof(uint));
                
                // Для одиночных обновлений можно использовать NamedBufferSubData (OpenGL 4.5+)
                // или классический Bind+SubData.
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsbo);
                GL.BufferSubData(BufferTarget.ShaderStorageBuffer, gpuOffset, sizeof(uint), ref air);
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
            }
        }
    }*/

    // Метод аварийного расширения (если памяти не хватило)
    private void ResizeBuffer()
    {
        int newCapacity = _currentCapacity * 2; 
        // ВАЖНО: Используем упакованный размер
        long oldBytes = (long)_currentCapacity * PackedChunkSizeInInts * sizeof(uint);
        long newBytes = (long)newCapacity * PackedChunkSizeInInts * sizeof(uint);

        Console.WriteLine($"[GpuRenderer] Resizing SSBO: {_currentCapacity}->{newCapacity} ({newBytes/1024/1024} MB)");

        int newSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, newSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)newBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        GL.CopyNamedBufferSubData(_voxelSsbo, newSsbo, IntPtr.Zero, IntPtr.Zero, (IntPtr)oldBytes);

        GL.DeleteBuffer(_voxelSsbo);
        _voxelSsbo = newSsbo;
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);

        for (int i = _currentCapacity; i < newCapacity; i++)
            _freeSlots.Push(i); 

        _currentCapacity = newCapacity;
    }

    // Интерфейс для внешнего мира
    public void UploadAllVisibleChunks() 
    {
        var chunks = _worldManager.GetChunksSnapshot();
        foreach(var c in chunks) NotifyChunkLoaded(c);
    }

    public void NotifyChunkLoaded(Chunk chunk) 
    {
        if (chunk != null && chunk.IsLoaded)
        {
            // Если чанк уже загружен - игнорируем (или можно добавить логику перезаливки)
            if (_loadedChunks.ContainsKey(chunk.Position)) return;

            if (_queuedChunkPositions.Add(chunk.Position)) 
                _chunksToUpload.Enqueue(chunk);
        }
    }
    
    public void UnloadChunk(Vector3i chunkPos) => _chunksToUnload.Enqueue(chunkPos);
    
    private void UpdateDynamicObjects()
    {
        var voxelObjects = _worldManager.GetAllVoxelObjects();
        int count = 0;
        foreach (var vo in voxelObjects)
        {
            if (count >= _tempGpuObjectsArray.Length) break;
            
            Matrix4 model = Matrix4.CreateFromQuaternion(vo.Rotation) * Matrix4.CreateTranslation(vo.Position);
            Matrix4 invModel = Matrix4.Invert(model);
            invModel.Transpose(); 

            var col = MaterialRegistry.GetColor(vo.Material);
            
            _tempGpuObjectsArray[count] = new GpuDynamicObject {
                InvModel = invModel,
                Color = new Vector4(col.r, col.g, col.b, 1.0f),
                BoxMin = new Vector4(vo.LocalBoundsMin, 0),
                BoxMax = new Vector4(vo.LocalBoundsMax, 0)
            };
            count++;
        }
        if (count > 0) {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, count * Marshal.SizeOf<GpuDynamicObject>(), _tempGpuObjectsArray);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        }
    }
    
    public void OnResize(int width, int height) => GL.Viewport(0, 0, width, height);
    
    public void Dispose()
    {
        GL.DeleteTexture(_pageTableTexture);
        GL.DeleteBuffer(_voxelSsbo);
        GL.DeleteBuffer(_dynamicObjectsBuffer);
        GL.DeleteVertexArray(_quadVao);
        _shader?.Dispose();
    }
}