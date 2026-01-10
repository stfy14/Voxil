using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using StbImageSharp;
using System.Runtime.InteropServices;
using System.IO;

public class GpuRaycastingRenderer : IDisposable
{
    // Вместо одного шейдера теперь используем систему
    private readonly ShaderSystem _shaderSystem;
    
    private Shader _gridComputeShader;
    private int _quadVao;
    private readonly WorldManager _worldManager;

    private int _pageTableTexture;
    
    // --- ДИНАМИЧЕСКИЕ БАНКИ ПАМЯТИ ---
    private int[] _voxelSsboBanks; 
    private int _activeBankCount = 1; 
    // Лимит одного банка (2 ГБ), чтобы не сломать int-индексацию в драйвере
    private const long MAX_BYTES_PER_BANK = 2147483648; 
    
    private int _maskSsbo;

    private int _gridHeadTexture;
    private int _linkedListSsbo;
    private int _atomicCounterBuffer;
    private int _dynamicObjectsBuffer;

    private int _beamFbo;
    private int _beamTexture;
    private int _beamWidth;
    private int _beamHeight;
    private const int BeamDivisor = 2;

    private const int ChunkVol = Constants.ChunkVolume;
    private const int PackedChunkSizeInInts = ChunkVol / 4;
    private const int ChunkMaskSizeInUlongs = Chunk.MasksCount;

    private readonly ulong[] _maskUploadBuffer = new ulong[ChunkMaskSizeInUlongs];
    private readonly uint[] _chunkUploadBuffer = new uint[PackedChunkSizeInInts];

    private const int PT_X = 512, PT_Y = 16, PT_Z = 512;
    private const int MASK_X = PT_X - 1, MASK_Y = PT_Y - 1, MASK_Z = PT_Z - 1;
    private const int OBJ_GRID_SIZE = 512;
    private float _gridCellSize;

    // Используем Queue для последовательной выдачи слотов (0, 1, 2...)
    private Queue<int> _freeSlots = new Queue<int>();
    private Dictionary<Vector3i, int> _allocatedChunks = new Dictionary<Vector3i, int>();
    private ConcurrentQueue<Chunk> _uploadQueue = new ConcurrentQueue<Chunk>();
    private HashSet<Vector3i> _chunksPendingUpload = new HashSet<Vector3i>();

    private readonly uint[] _cpuPageTable = new uint[PT_X * PT_Y * PT_Z];
    private bool _pageTableDirty = true;
    
    private int _currentCapacity;
    private float _totalTime = 0f;
    private int _noiseTexture;
    private Vector3 _lastGridOrigin;
    private GpuDynamicObject[] _tempGpuObjectsArray = new GpuDynamicObject[4096];
    private bool _memoryFullWarned = false;

    public int TotalVramMb { get; private set; } = 4096;
    public long CurrentAllocatedBytes { get; private set; } = 0;
    private bool _reallocationPending = false;

    [StructLayout(LayoutKind.Sequential)]
    struct GpuDynamicObject { public Matrix4 Model; public Matrix4 InvModel; public Vector4 Color; public Vector4 BoxMin; public Vector4 BoxMax; }

    public GpuRaycastingRenderer(WorldManager worldManager)
    {
        _worldManager = worldManager;
        _shaderSystem = new ShaderSystem(); // Инициализация системы шейдеров
        _gridCellSize = Math.Max(Constants.VoxelSize, 0.5f);
        TotalVramMb = DetectTotalVram();
    }

    public void Load()
    {
        // Компилируем начальный шейдер (1 банк)
        _shaderSystem.Compile(1);
        
        _gridComputeShader = new Shader("Shaders/grid_update.comp");
        
        _quadVao = GL.GenVertexArray(); 
        GL.BindVertexArray(_quadVao);
        int dummy = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ArrayBuffer, dummy);
        GL.BufferData(BufferTarget.ArrayBuffer, sizeof(float)*8, new float[]{-1,-1,1,-1,-1,1,1,1}, BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0); GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        _noiseTexture = LoadTexture("Shaders/Images/water_noise.png");

        InitializeBuffers();
        UploadAllVisibleChunks();
    }

    private int DetectTotalVram()
    {
        try { GL.GetInteger((GetPName)0x9048, out int kb); if (kb > 0) return kb/1024; } catch {}
        return 4096;
    }
    
    // --- СИСТЕМА ПЕРЕЗАГРУЗКИ (REALLOCATION) ---
    public void RequestReallocation()
    {
        Console.WriteLine("[Renderer] Reallocation requested.");
        CleanupBuffers(); 
        // Принудительная чистка мусора
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        _reallocationPending = true; _currentCapacity = 0;
    }

    public bool IsReallocationPending() => _reallocationPending;
    
    public void PerformReallocation()
    {
        if (!_reallocationPending) return;
        InitializeBuffers();
        _reallocationPending = false;
    }

    public long CalculateMemoryBytesForDistance(int distance)
    {
        int chunks = (distance * 2 + 1) * (distance * 2 + 1) * WorldManager.WorldHeightChunks;
        return (long)chunks * (PackedChunkSizeInInts * 4 + ChunkMaskSizeInUlongs * 8);
    }

    public void InitializeBuffers()
    {
        int dist = Math.Max(GameSettings.RenderDistance, 4);
        int maxDist = dist + 2;
        int requestedChunks = (maxDist * 2 + 1) * (maxDist * 2 + 1) * WorldManager.WorldHeightChunks;

        // 1. Считаем необходимую память
        long bytesPerChunkVoxel = PackedChunkSizeInInts * 4;
        long totalVBytes = (long)requestedChunks * bytesPerChunkVoxel;
        long mBytes = (long)requestedChunks * ChunkMaskSizeInUlongs * 8;

        // 2. Вычисляем количество банков (делим на 2ГБ с округлением вверх)
        int neededBanks = (int)(totalVBytes / MAX_BYTES_PER_BANK) + 1;
        
        // Лимит банков (например, 32 = 64 ГБ, хватит всем)
        if (neededBanks > 32) neededBanks = 32;

        Console.WriteLine($"[Renderer] InitBuffers: Need {totalVBytes / 1024 / 1024} MB voxels. Banks needed: {neededBanks}");

        // Проверяем, изменилась ли конфигурация
        bool banksConfigChanged = (_voxelSsboBanks == null) || (_voxelSsboBanks.Length != neededBanks);
        
        // Если ничего не изменилось - выходим
        if (!banksConfigChanged && _currentCapacity == requestedChunks) return;

        // 3. Удаляем старые банки, если конфигурация меняется
        if (banksConfigChanged && _voxelSsboBanks != null)
        {
            Console.WriteLine("[Renderer] Bank configuration changed. Recreating GL objects...");
            for (int i = 0; i < _voxelSsboBanks.Length; i++) if(_voxelSsboBanks[i] != 0) GL.DeleteBuffer(_voxelSsboBanks[i]);
            _voxelSsboBanks = null;
        }

        // 4. Создаем массив банков
        if (_voxelSsboBanks == null)
        {
            _voxelSsboBanks = new int[neededBanks];
            for (int i = 0; i < neededBanks; i++) _voxelSsboBanks[i] = GL.GenBuffer();
            
            // Вспомогательные буферы создаем один раз
            if (_maskSsbo == 0) _maskSsbo = GL.GenBuffer();
            if (_pageTableTexture == 0) _pageTableTexture = GL.GenTexture();
            CreateAuxBuffers();
        }

        _activeBankCount = neededBanks;
        _currentCapacity = requestedChunks;
        CurrentAllocatedBytes = totalVBytes + mBytes;

        // 5. Распределяем память
        // Делим чанки поровну между банками для баланса
        long chunksPerBank = (requestedChunks + neededBanks - 1) / neededBanks;
        long bytesPerBank = chunksPerBank * bytesPerChunkVoxel;

        Console.WriteLine($"[Renderer] Allocating {_activeBankCount} banks. ~{bytesPerBank / 1024 / 1024} MB each.");

        for (int i = 0; i < _activeBankCount; i++)
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsboBanks[i]);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)bytesPerBank, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            
            // Привязываем к binding points 10, 11, 12...
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10 + i, _voxelSsboBanks[i]);
        }

        // Mask Buffer (один кусок, binding 5)
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _maskSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)mBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo);

        // PageTable
        GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32ui, PT_X, PT_Y, PT_Z, 0, PixelFormat.RedInteger, PixelType.UnsignedInt, _cpuPageTable);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        
        ResetAllocationLogic(_currentCapacity);

        // ВАЖНО: Если количество банков изменилось, нужно перекомпилировать шейдер
        if (banksConfigChanged)
        {
            _shaderSystem.Compile(_activeBankCount);
        }
    }

    private void ResetAllocationLogic(int capacity)
    {
        _freeSlots.Clear(); _allocatedChunks.Clear();
        // Заполняем очередь: 0, 1, 2...
        for (int i = 0; i < capacity; i++) _freeSlots.Enqueue(i);
        Array.Fill(_cpuPageTable, 0xFFFFFFFF);
        _chunksPendingUpload.Clear();
        while(!_uploadQueue.IsEmpty) _uploadQueue.TryDequeue(out _);
        _memoryFullWarned = false; _pageTableDirty = true;
    }

    private void CreateAuxBuffers()
    {
        if (_gridHeadTexture == 0) { _gridHeadTexture = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture3D, _gridHeadTexture); GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32i, OBJ_GRID_SIZE, OBJ_GRID_SIZE, OBJ_GRID_SIZE, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero); GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest); GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest); }
        if (_linkedListSsbo == 0) { _linkedListSsbo = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _linkedListSsbo); GL.BufferData(BufferTarget.ShaderStorageBuffer, 2*1024*1024*8, IntPtr.Zero, BufferUsageHint.DynamicDraw); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo); }
        if (_atomicCounterBuffer == 0) { _atomicCounterBuffer = GL.GenBuffer(); GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer); GL.BufferData(BufferTarget.AtomicCounterBuffer, 4, IntPtr.Zero, BufferUsageHint.DynamicDraw); GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, _atomicCounterBuffer); }
        if (_dynamicObjectsBuffer == 0) { _dynamicObjectsBuffer = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer); GL.BufferData(BufferTarget.ShaderStorageBuffer, 4096 * Marshal.SizeOf<GpuDynamicObject>(), IntPtr.Zero, BufferUsageHint.DynamicDraw); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer); }
        if (_beamFbo == 0) _beamFbo = GL.GenFramebuffer(); if (_beamTexture == 0) _beamTexture = GL.GenTexture();
    }

    private void CleanupBuffers()
    {
        GL.UseProgram(0); GL.BindVertexArray(0); GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        // Отвязываем много слотов (с запасом)
        for(int i=0; i<32; i++) GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, i, 0);
        GL.Flush(); GL.Finish();
        
        // Удаляем динамические банки
        if (_voxelSsboBanks != null) {
            for(int i=0; i<_voxelSsboBanks.Length; i++) {
                if (_voxelSsboBanks[i] != 0) GL.DeleteBuffer(_voxelSsboBanks[i]);
            }
            _voxelSsboBanks = null;
        }
        
        if (_maskSsbo != 0) { GL.DeleteBuffer(_maskSsbo); _maskSsbo = 0; }
        if (_pageTableTexture != 0) { GL.DeleteTexture(_pageTableTexture); _pageTableTexture = 0; }
        
        CurrentAllocatedBytes = 0; _currentCapacity = 0;
        System.Threading.Thread.Sleep(50);
    }
    
    // --- ЛОГИКА ЗАГРУЗКИ ---
    
    private void UploadChunkVoxels(Chunk chunk, int globalSlot) 
    { 
        chunk.ReadDataUnsafe((srcVoxels, srcMasks) => { 
            if (srcVoxels == null) return; 
            
            // Вычисляем, в какой банк и по какому смещению писать
            int bankIndex = globalSlot % _activeBankCount;
            int localSlot = globalSlot / _activeBankCount;
            
            // Загрузка в конкретный банк
            System.Buffer.BlockCopy(srcVoxels, 0, _chunkUploadBuffer, 0, ChunkVol); 
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsboBanks[bankIndex]); 
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)((long)localSlot * PackedChunkSizeInInts * 4), PackedChunkSizeInInts * 4, _chunkUploadBuffer); 
            
            // Маски всегда в одном буфере (они маленькие)
            if (srcMasks != null) { 
                System.Buffer.BlockCopy(srcMasks, 0, _maskUploadBuffer, 0, ChunkMaskSizeInUlongs * 8); 
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _maskSsbo); 
                GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)((long)globalSlot * ChunkMaskSizeInUlongs * 8), ChunkMaskSizeInUlongs * 8, _maskUploadBuffer); 
            } 
        }); 
    }

    public void NotifyChunkLoaded(Chunk chunk) 
    { 
        if (chunk == null || !chunk.IsLoaded || chunk.SolidCount == 0) return; 
        if (!_allocatedChunks.ContainsKey(chunk.Position)) 
        { 
            if (_freeSlots.Count > 0) 
            { 
                int slot = _freeSlots.Dequeue();
                _allocatedChunks[chunk.Position] = slot; 
                _cpuPageTable[GetPageTableIndex(chunk.Position)] = (uint)slot; 
                _pageTableDirty = true; 
            } 
            else if (!_memoryFullWarned) { Console.WriteLine("GPU FULL"); _memoryFullWarned = true; return; } 
        } 
        lock(_chunksPendingUpload) { if (_chunksPendingUpload.Add(chunk.Position)) _uploadQueue.Enqueue(chunk); } 
    }

    public void NotifyChunkModified(Chunk chunk) => NotifyChunkLoaded(chunk);
    
    public void UnloadChunk(Vector3i pos) 
    { 
        if (_allocatedChunks.TryGetValue(pos, out int slot)) 
        { 
            _freeSlots.Enqueue(slot); 
            _allocatedChunks.Remove(pos); 
            _cpuPageTable[GetPageTableIndex(pos)] = 0xFFFFFFFF; 
            _pageTableDirty = true; 
            lock(_chunksPendingUpload) { _chunksPendingUpload.Remove(pos); } 
        } 
    }

    public void Update(float deltaTime) 
    { 
        _totalTime += deltaTime; 
        int limit = GameSettings.GpuUploadSpeed; 
        while (limit > 0 && _uploadQueue.TryDequeue(out var chunk)) 
        { 
            if (_allocatedChunks.TryGetValue(chunk.Position, out int slot)) 
            { 
                lock(_chunksPendingUpload) _chunksPendingUpload.Remove(chunk.Position);
                if (chunk.IsLoaded) { UploadChunkVoxels(chunk, slot); limit--; }
            }
        } 
        if (_pageTableDirty) 
        { 
            GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture); 
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, PT_X, PT_Y, PT_Z, PixelFormat.RedInteger, PixelType.UnsignedInt, _cpuPageTable); 
            _pageTableDirty = false; 
        } 
        UpdateDynamicObjectsAndGrid(); 
    }

    private int GetPageTableIndex(Vector3i p) => (p.X & MASK_X) + PT_X * ((p.Y & MASK_Y) + PT_Y * (p.Z & MASK_Z));
    
    private void UpdateDynamicObjectsAndGrid() 
{ 
    // 1. Получаем список всех активных воксельных объектов
    var voxelObjects = _worldManager.GetAllVoxelObjects(); 
    int count = voxelObjects.Count; 

    // 2. Если объектов стало больше, чем вмещает текущий массив, расширяем его
    if (count > _tempGpuObjectsArray.Length) 
    { 
        // Увеличиваем массив с запасом (+1024)
        Array.Resize(ref _tempGpuObjectsArray, count + 1024); 
        
        // Пересоздаем буфер на GPU под новый размер
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer); 
        GL.BufferData(BufferTarget.ShaderStorageBuffer, 
                      _tempGpuObjectsArray.Length * Marshal.SizeOf<GpuDynamicObject>(), 
                      IntPtr.Zero, 
                      BufferUsageHint.DynamicDraw); 
    } 

    // 3. Заполняем массив данных для GPU (позиция, поворот, цвет, AABB)
    if (count > 0) 
    { 
        // Используем Parallel.For для ускорения математики при большом кол-ве объектов
        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }; 
        
        Parallel.For(0, count, options, i => { 
            var vo = voxelObjects[i]; 
            
            // Матрица модели: Сдвиг ЦМ в 0 -> Вращение -> Сдвиг в позицию мира
            Matrix4 model = Matrix4.CreateTranslation(-vo.LocalCenterOfMass) * 
                            Matrix4.CreateFromQuaternion(vo.Rotation) * 
                            Matrix4.CreateTranslation(vo.Position); 
            
            var col = MaterialRegistry.GetColor(vo.Material); 
            
            _tempGpuObjectsArray[i] = new GpuDynamicObject { 
                Model = model, 
                InvModel = Matrix4.Invert(model), 
                Color = new Vector4(col.r, col.g, col.b, 1.0f), 
                // Передаем локальные границы (AABB) для ускорения лучей
                BoxMin = new Vector4(vo.LocalBoundsMin, 0), 
                BoxMax = new Vector4(vo.LocalBoundsMax, 0) 
            }; 
        }); 

        // Заливаем данные в SSBO (Binding 2)
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer); 
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, 
                         IntPtr.Zero, 
                         count * Marshal.SizeOf<GpuDynamicObject>(), 
                         _tempGpuObjectsArray); 
    } 

    // 4. Рассчитываем положение сетки ускорения (Grid)
    // Сетка "прилипает" к игроку, но движется дискретно (шагами), чтобы не было дрожания
    float snap = _gridCellSize; 
    Vector3 playerPos = _worldManager.GetPlayerPosition(); 
    Vector3 snappedCenter = new Vector3(
        (float)Math.Floor(playerPos.X / snap) * snap, 
        (float)Math.Floor(playerPos.Y / snap) * snap, 
        (float)Math.Floor(playerPos.Z / snap) * snap
    ); 
    float halfExtent = (OBJ_GRID_SIZE * _gridCellSize) / 2.0f; 
    _lastGridOrigin = snappedCenter - new Vector3(halfExtent); 

    // 5. ОЧИСТКА СТРУКТУР ПЕРЕД COMPUTE SHADER
    // Сбрасываем счетчик узлов связного списка в 0
    uint zero = 0; 
    GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer); 
    GL.BufferSubData(BufferTarget.AtomicCounterBuffer, IntPtr.Zero, sizeof(uint), ref zero); 
    
    // Очищаем 3D текстуру (головы списков) значением 0
    GL.ClearTexImage(_gridHeadTexture, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero); 

    // 6. Запускаем Compute Shader для построения сетки
    if (count > 0) 
    { 
        _gridComputeShader.Use(); 
        _gridComputeShader.SetInt("uObjectCount", count); 
        _gridComputeShader.SetVector3("uGridOrigin", _lastGridOrigin); 
        _gridComputeShader.SetFloat("uGridStep", _gridCellSize); 
        _gridComputeShader.SetInt("uGridSize", OBJ_GRID_SIZE); 

        // Привязываем ресурсы
        // Binding 1: Текстура-голова (R/W)
        GL.BindImageTexture(1, _gridHeadTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32i); 
        // Binding 2: Данные объектов (SSBO)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer); 
        // Binding 3: Узлы связного списка (SSBO)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo); 
        // Binding 4: Атомарный счетчик
        GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, _atomicCounterBuffer); 

        // Запускаем (по 64 потока в группе)
        GL.DispatchCompute((count + 63) / 64, 1, 1); 

        // Ждем завершения записи в память, чтобы фрагментный шейдер увидел актуальные данные
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | 
                         MemoryBarrierFlags.AtomicCounterBarrierBit | 
                         MemoryBarrierFlags.ShaderStorageBarrierBit); 
    } 
}
    
    public void Render(Camera cam) 
    { 
        if (_reallocationPending || _voxelSsboBanks == null) return;
        
        _shaderSystem.Use(); // Используем систему шейдеров
        var shader = _shaderSystem.RaycastShader;
        if (shader == null) return;

        shader.SetVector3("uCamPos", cam.Position); 
        shader.SetMatrix4("uView", cam.GetViewMatrix()); shader.SetMatrix4("uProjection", cam.GetProjectionMatrix()); 
        shader.SetMatrix4("uInvView", Matrix4.Invert(cam.GetViewMatrix())); shader.SetMatrix4("uInvProjection", Matrix4.Invert(cam.GetProjectionMatrix())); 
        shader.SetFloat("uRenderDistance", _worldManager.GetViewRangeInMeters()); 
        shader.SetInt("uMaxRaySteps", (int)(GameSettings.RenderDistance * 8) + 128); 
        shader.SetVector3("uSunDir", Vector3.Normalize(new Vector3(0.2f, 0.4f, 0.8f))); 
        shader.SetFloat("uTime", _totalTime); 
        shader.SetInt("uShowDebugHeatmap", GameSettings.ShowDebugHeatmap ? 1 : 0);
        shader.SetInt("uSoftShadowSamples", GameSettings.SoftShadowSamples);

        Vector3 p = cam.Position; int sz = Constants.ChunkSizeWorld; 
        int cx = (int)Math.Floor(p.X/sz), cy = (int)Math.Floor(p.Y/sz), cz = (int)Math.Floor(p.Z/sz); 
        int r = GameSettings.RenderDistance + 2; 
        shader.SetInt("uBoundMinX", cx-r); shader.SetInt("uBoundMinY", 0); shader.SetInt("uBoundMinZ", cz-r); 
        shader.SetInt("uBoundMaxX", cx+r); shader.SetInt("uBoundMaxY", WorldManager.WorldHeightChunks); shader.SetInt("uBoundMaxZ", cz+r); 
        
        GL.BindImageTexture(0, _pageTableTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32ui); 
        
        // --- ДИНАМИЧЕСКАЯ ПРИВЯЗКА БАНКОВ ---
        for(int i = 0; i < _activeBankCount; i++) {
            // Привязываем буферы к точкам 10, 11, 12...
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10 + i, _voxelSsboBanks[i]);
        }
        
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo); 
        
        // --- ОСТАЛЬНЫЕ БУФЕРЫ ---
        GL.BindImageTexture(1, _gridHeadTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32i);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo);
        
        shader.SetTexture("uNoiseTexture", _noiseTexture, TextureUnit.Texture0);
        shader.SetVector3("uGridOrigin", _lastGridOrigin); shader.SetFloat("uGridStep", _gridCellSize); 
        shader.SetInt("uGridSize", OBJ_GRID_SIZE); shader.SetInt("uObjectCount", _worldManager.GetAllVoxelObjects().Count);

        GL.BindVertexArray(_quadVao); 
        
        if (GameSettings.BeamOptimization) { 
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _beamFbo); GL.Viewport(0, 0, _beamWidth, _beamHeight); GL.Clear(ClearBufferMask.ColorBufferBit); 
            shader.SetInt("uIsBeamPass", 1); GL.Disable(EnableCap.DepthTest); GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4); 
        } 
        
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); 
        GL.Viewport(0, 0, _beamWidth * BeamDivisor, _beamHeight * BeamDivisor); 
        shader.SetInt("uIsBeamPass", 0); 
        shader.SetTexture("uBeamTexture", _beamTexture, TextureUnit.Texture1); 
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4); 
        GL.Enable(EnableCap.DepthTest); 
        GL.BindVertexArray(0); 
    }
    
    public void OnResize(int w, int h) { _beamWidth = w/2; _beamHeight = h/2; GL.Viewport(0,0,w,h); }
    
    // Прокси метод для UI, который просит систему перекомпилировать шейдер
    public void ReloadShader() 
    { 
        _shaderSystem.Compile(_activeBankCount);
    }
    
    public void UploadAllVisibleChunks() { foreach (var c in _worldManager.GetChunksSnapshot()) NotifyChunkLoaded(c); }
    
    public void Dispose() 
    { 
        CleanupBuffers(); 
        _quadVao = 0; 
        _shaderSystem?.Dispose(); 
        _gridComputeShader?.Dispose(); 
    }

    private int LoadTexture(string path) { if (!File.Exists(path)) return 0; int handle = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, handle); StbImage.stbi_set_flip_vertically_on_load(1); using (Stream stream = File.OpenRead(path)) { ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha); GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, image.Width, image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, image.Data); } GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear); GL.GenerateMipmap(GenerateMipmapTarget.Texture2D); return handle; }
}