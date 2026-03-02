using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq; 
using StbImageSharp; // Добавлен юзинг

public class GpuRaycastingRenderer : IDisposable
{
    private readonly ShaderSystem _shaderSystem;
    private Shader _gridComputeShader;
    private int _quadVao;
    private readonly WorldManager _worldManager;

    private int _pageTableTexture;
    
    // --- ПАМЯТЬ SSBO ---
    private const int MAX_TOTAL_BANKS = 8;
    // Размер 256 МБ.
    private const long BANK_SIZE_BYTES = 268435456;

    private int[] _voxelSsboBanks = new int[MAX_TOTAL_BANKS]; 
    private int _chunksPerBank;
    private int _activeBankCount = 0; 
    private int _dummySSBO; 
    
    private int _maskSsbo;

    private int _gridHeadTexture;
    private int _linkedListSsbo;
    private int _atomicCounterBuffer;
    private int _dynamicObjectsBuffer;

    private int _beamFbo;
    private int _beamTexture;
    private int _beamWidth;
    private int _beamHeight;
    private const int BeamDivisor = 4;
    private int _windowWidth;
    private int _windowHeight;

    private const int ChunkVol = Constants.ChunkVolume;
    private const int PackedChunkSizeInInts = ChunkVol / 4;
    private const int ChunkMaskSizeInUlongs = Chunk.MasksCount;

    private readonly ulong[] _maskUploadBuffer = new ulong[ChunkMaskSizeInUlongs];
    private readonly uint[] _chunkUploadBuffer = new uint[PackedChunkSizeInInts];

    private const int PT_X = 512, PT_Y = 16, PT_Z = 512;
    private const int MASK_X = PT_X - 1, MASK_Y = PT_Y - 1, MASK_Z = PT_Z - 1;
    private const int OBJ_GRID_SIZE = 64;
    private float _gridCellSize;

    private Queue<int> _freeSlots = new Queue<int>();
    private Dictionary<Vector3i, int> _allocatedChunks = new Dictionary<Vector3i, int>();
    private ConcurrentQueue<Chunk> _uploadQueue = new ConcurrentQueue<Chunk>();
    private HashSet<Vector3i> _chunksPendingUpload = new HashSet<Vector3i>();

    private const uint SOLID_CHUNK_FLAG = 0x80000000;
    private const int SOLITARY_SLOT_INDEX = -1; 

    private readonly uint[] _cpuPageTable = new uint[PT_X * PT_Y * PT_Z];
    private bool _pageTableDirty = true;
    
    private float _totalTime = 0f;
    private int _noiseTexture;
    private Vector3 _lastGridOrigin;
    private GpuDynamicObject[] _tempGpuObjectsArray = new GpuDynamicObject[4096];
    
    public int TotalVramMb { get; private set; } = 4096;
    public long CurrentAllocatedBytes { get; private set; } = 0;
    private bool _reallocationPending = false;

    //Render Scale
    private int _mainFbo;
    private int _mainColorTexture;
    private int _mainDepthTexture;
    private int _renderWidth;
    private int _renderHeight;

    // TAA
    private Shader _taaShader;
    private int[] _historyFbo = new int[2];
    private int[] _historyTexture = new int[2];
    private int _historyWriteIndex = 0;
    private long _frameIndex = 0;
    // ТОЛЬКО ОДНА МАТРИЦА - ЧИСТАЯ (БЕЗ ДЖИТТЕРА)
    private Matrix4 _prevCleanViewProjection = Matrix4.Identity;
    private Vector2 _prevJitterNDC = Vector2.Zero;
    private bool _resetTaaHistory = true;
    
    private VoxelObject _currentViewModel;

    // Стандартная последовательность Галтона для джиттера
    private readonly Vector2[] _haltonSequence = new Vector2[] {
        new(0.5f, 0.333333f), new(0.25f, 0.666667f), new(0.75f, 0.111111f), new(0.125f, 0.444444f),
        new(0.625f, 0.777778f), new(0.375f, 0.222222f), new(0.875f, 0.555556f), new(0.0625f, 0.888889f)
    };

    //Compute Shader Updater
    private int _editSsbo;
    private ConcurrentQueue<GpuVoxelEdit> _editQueue = new ConcurrentQueue<GpuVoxelEdit>();
    private GpuVoxelEdit[] _editUploadArray = new GpuVoxelEdit[1024]; [StructLayout(LayoutKind.Sequential)]
    struct GpuVoxelEdit
    {
        public uint ChunkSlot;
        public uint VoxelIndex;
        public uint NewMaterial;
        public uint Padding; // GLSL std430 выравнивание до 16 байт
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GpuDynamicObject { public Matrix4 Model; public Matrix4 InvModel; public Vector4 Color; public Vector4 BoxMin; public Vector4 BoxMax; }

    public GpuRaycastingRenderer(WorldManager worldManager)
    {
        _worldManager = worldManager;
        _shaderSystem = new ShaderSystem(); 
        _gridCellSize = Math.Max(Constants.VoxelSize * 16.0f, 2.0f);
        TotalVramMb = DetectTotalVram();
    }

    public void Load()
    {
        _gridComputeShader = new Shader("Shaders/grid_update.comp");
        _taaShader = new Shader("Shaders/raycast.vert", "Shaders/taa.frag");

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
    
    public void SetViewModel(VoxelObject viewModel)
    {
        _currentViewModel = viewModel;
    }
    
    public void RequestReallocation() { CleanupBuffers(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); _reallocationPending = true; }
    public bool IsReallocationPending() => _reallocationPending;
    public void PerformReallocation() { if (!_reallocationPending) return; InitializeBuffers(); _reallocationPending = false; }

    public long CalculateMemoryBytesForDistance(int distance)
    {
        int chunks = (distance * 2 + 1) * (distance * 2 + 1) * WorldManager.WorldHeightChunks;
        return (long)chunks * (PackedChunkSizeInInts * 4 + ChunkMaskSizeInUlongs * 8);
    }

    public void ApplyRenderScale()
    {
        // Принудительно вызываем пересоздание FBO с текущими размерами окна,
        // но внутри OnResize теперь учтется новый GameSettings.RenderScale
        OnResize(_windowWidth, _windowHeight);
    }

    public void InitializeBuffers()
    {
        if (_dummySSBO == 0)
        {
            _dummySSBO = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dummySSBO);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, 4, IntPtr.Zero, BufferUsageHint.StaticDraw);
        }

        CleanupBanks();
        // Сначала создаем первую банку
        if (!TryAddBank()) throw new Exception("Critical: Failed to allocate even the first VRAM bank!");

        // А ТЕПЕРЬ считаем, сколько чанков влезет в ЭТОТ размер (128 МБ)
        // PackedChunkSizeInInts * 4 = размер одного чанка в байтах (32768 байт)
        _chunksPerBank = (int)(BANK_SIZE_BYTES / (PackedChunkSizeInInts * 4));

        Console.WriteLine($"[Init] Bank Size: {BANK_SIZE_BYTES / 1024 / 1024} MB");
        Console.WriteLine($"[Init] Chunks per Bank: {_chunksPerBank}");
        Console.WriteLine($"[Init] Max World Capacity: {_chunksPerBank * MAX_TOTAL_BANKS} chunks");

        BindAllBuffers();

        int dist = Math.Max(GameSettings.RenderDistance, 4);
        int requestedChunks = (dist * 2 + 1) * (dist * 2 + 1) * WorldManager.WorldHeightChunks;
        long maxChunksForMasks = 262144; // 256k чанков * 4кб = 1 ГБ (многовато, но безопасно для RTX 4060/5060)
        long mBytes = maxChunksForMasks * ChunkMaskSizeInUlongs * 8;

        if (_maskSsbo == 0) _maskSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _maskSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)mBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo);

        if (_pageTableTexture == 0) _pageTableTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32ui, PT_X, PT_Y, PT_Z, 0, PixelFormat.RedInteger, PixelType.UnsignedInt, _cpuPageTable);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);

        CreateAuxBuffers();
        _shaderSystem.Compile(MAX_TOTAL_BANKS, _chunksPerBank);
        ResetAllocationLogic();
    }

    private void CleanupBanks()
    {
        for (int i = 0; i < MAX_TOTAL_BANKS; i++)
        {
            if (_voxelSsboBanks[i] != 0) GL.DeleteBuffer(_voxelSsboBanks[i]);
            _voxelSsboBanks[i] = 0;
        }
        _activeBankCount = 0;
    }

    private void BindAllBuffers()
    {
        for (int i = 0; i < MAX_TOTAL_BANKS; i++)
        {
            int handle = (i < _activeBankCount) ? _voxelSsboBanks[i] : _dummySSBO;
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 8 + i, handle);
        }
    }

    private void ResetAllocationLogic()
    {
        _freeSlots.Clear(); _allocatedChunks.Clear();
        int chunksPerBank = (int)(BANK_SIZE_BYTES / (PackedChunkSizeInInts * 4));
        for (int b = 0; b < _activeBankCount; b++)
        {
            int start = b * chunksPerBank;
            for(int i=0; i<chunksPerBank; i++) _freeSlots.Enqueue(start + i);
        }

        Array.Fill(_cpuPageTable, 0xFFFFFFFF);
        _chunksPendingUpload.Clear();
        while(!_uploadQueue.IsEmpty) _uploadQueue.TryDequeue(out _);
        _pageTableDirty = true;
        CurrentAllocatedBytes = _activeBankCount * BANK_SIZE_BYTES;
    }

    private bool TryAddBank()
    {
        if (_activeBankCount >= MAX_TOTAL_BANKS) return false;

        try
        {
            int newIndex = _activeBankCount;
            int newBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, newBuffer);
            // Пытаемся выделить 256 МБ. Драйвер сам решит, куда.
            GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)BANK_SIZE_BYTES, IntPtr.Zero, BufferUsageHint.DynamicDraw);

            // Проверка на ошибку памяти
            if (GL.GetError() == ErrorCode.OutOfMemory)
            {
                GL.DeleteBuffer(newBuffer);
                Console.WriteLine("[VRAM] Out Of Memory!");
                return false;
            }

            _voxelSsboBanks[newIndex] = newBuffer;
            _activeBankCount++;
            CurrentAllocatedBytes += BANK_SIZE_BYTES;

            // Биндим в слот 8 + index (8, 9, ... 15)
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 8 + newIndex, newBuffer);
            Console.WriteLine($"[VRAM] Added Bank #{newIndex} (Binding {8 + newIndex}). Total: {_activeBankCount}");
            return true;
        }
        catch { return false; }
    }

    private int GetAvailableVramKb()
    {
        try {
            int kb = 0;
            GL.GetInteger((GetPName)0x9049, out kb); 
            if (kb > 0) return kb;
            int[] param = new int[4];
            GL.GetInteger((GetPName)0x87FC, param);
            if (param[0] > 0) return param[0];
        } catch {}
        return 1024 * 1024 * 8; 
    }

    private int GetSlotOrPanic()
    {
    // 1. Если есть свободные слоты - берем их
        if (_freeSlots.Count > 0) return _freeSlots.Dequeue();

        // 2. Если слотов нет, пробуем создать новую банку
        if (TryAddBank())
        {
            // ВАЖНО: Используем поле класса _chunksPerBank, которое мы считали при старте
            // Новая банка имеет индекс (_activeBankCount - 1), так как TryAddBank уже сделал инкремент
            int startSlotIndex = (_activeBankCount - 1) * _chunksPerBank;
        
            // Добавляем все новые слоты в очередь
            for (int i = 0; i < _chunksPerBank; i++) 
            {
                _freeSlots.Enqueue(startSlotIndex + i);
            }

            Console.WriteLine($"[VRAM] Bank expanded. New free slots: {_freeSlots.Count}");
            return _freeSlots.Dequeue();
        }

        // 3. ПАНИКА (Если видеопамять кончилась)
        Console.WriteLine("[VRAM] PANIC: GPU Memory Full! Reducing Render Distance!");
        if (GameSettings.RenderDistance > 6) GameSettings.RenderDistance -= 2;

        Vector3 playerPos = _worldManager.GetPlayerPosition(); // Убрали .ToOpenTK()
        Vector3i playerChunkPos = new Vector3i((int)(playerPos.X / Constants.ChunkSizeWorld), 0, (int)(playerPos.Z / Constants.ChunkSizeWorld));
        
        int newDistSq = GameSettings.RenderDistance * GameSettings.RenderDistance;
        List<Vector3i> toEvict = new List<Vector3i>();

        foreach(var kvp in _allocatedChunks)
        {
            if (kvp.Value == SOLITARY_SLOT_INDEX) continue;
            int dx = kvp.Key.X - playerChunkPos.X;
            int dz = kvp.Key.Z - playerChunkPos.Z;
            if (dx*dx + dz*dz > newDistSq) toEvict.Add(kvp.Key);
        }

        toEvict.Sort((a, b) => {
            float da = (a - playerChunkPos).EuclideanLengthSquared;
            float db = (b - playerChunkPos).EuclideanLengthSquared;
            return db.CompareTo(da);
        });

        if (toEvict.Count == 0)
        {
             var furthest = _allocatedChunks
                .Where(x => x.Value != SOLITARY_SLOT_INDEX)
                .OrderByDescending(x => (x.Key - playerChunkPos).EuclideanLengthSquared)
                .FirstOrDefault();
             if (furthest.Key != default) toEvict.Add(furthest.Key);
             else throw new Exception("VRAM Critical: No chunks to evict!");
        }

        Vector3i victim = toEvict[0];
        int freedSlot = _allocatedChunks[victim];
        _allocatedChunks.Remove(victim);
        _cpuPageTable[GetPageTableIndex(victim)] = 0xFFFFFFFF;
        _pageTableDirty = true;

        return freedSlot;
    }

    public void NotifyChunkLoaded(Chunk chunk) 
    { 
        if (chunk == null || !chunk.IsLoaded || chunk.SolidCount == 0) return; 

        bool isUniform = chunk.IsFullyUniform(out var mat);
        
        if (_allocatedChunks.TryGetValue(chunk.Position, out int existingSlot))
        {
            if (existingSlot == SOLITARY_SLOT_INDEX && !isUniform)
            {
                _allocatedChunks.Remove(chunk.Position); 
            }
            else if (existingSlot != SOLITARY_SLOT_INDEX && isUniform)
            {
                _freeSlots.Enqueue(existingSlot);
                _allocatedChunks[chunk.Position] = SOLITARY_SLOT_INDEX;
                _cpuPageTable[GetPageTableIndex(chunk.Position)] = SOLID_CHUNK_FLAG | (uint)mat;
                _pageTableDirty = true;
                return;
            }
            else if (!isUniform) 
            {
                lock(_chunksPendingUpload) { if (_chunksPendingUpload.Add(chunk.Position)) _uploadQueue.Enqueue(chunk); }
                return;
            }
            else return; 
        }

        if (!_allocatedChunks.ContainsKey(chunk.Position)) 
        { 
            if (isUniform)
            {
                _allocatedChunks[chunk.Position] = SOLITARY_SLOT_INDEX;
                _cpuPageTable[GetPageTableIndex(chunk.Position)] = SOLID_CHUNK_FLAG | (uint)mat;
                _pageTableDirty = true;
            }
            else
            {
                int slot = GetSlotOrPanic();
                _allocatedChunks[chunk.Position] = slot; 
                _cpuPageTable[GetPageTableIndex(chunk.Position)] = (uint)slot; 
                _pageTableDirty = true; 
                lock(_chunksPendingUpload) { if (_chunksPendingUpload.Add(chunk.Position)) _uploadQueue.Enqueue(chunk); } 
            }
        } 
    }

    // === ВЕРНУЛ МЕТОД UPDATE ===
    public void Update(float deltaTime)
    {
        _totalTime += deltaTime;

        // =========================================================
        // 1. ПРИМЕНЯЕМ ТОЧЕЧНЫЕ РЕДАКТИРОВАНИЯ (ДЕЛТА-ОБНОВЛЕНИЯ)
        // =========================================================
        int editsToApply = Math.Min(_editQueue.Count, 1024);
        if (editsToApply > 0)
        {
            for (int i = 0; i < editsToApply; i++)
            {
                _editQueue.TryDequeue(out _editUploadArray[i]);
            }

            // Отправляем массив правок в SSBO
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _editSsbo);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, editsToApply * 16, _editUploadArray);

            var editShader = _shaderSystem.EditUpdaterShader;
            if (editShader != null)
            {
                editShader.Use();
                editShader.SetInt("uEditCount", editsToApply);

                // Подключаем банки вокселей (b0, b1...), маски и буфер правок
                BindAllBuffers();
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, _editSsbo);

                // Запускаем Compute Shader (размер рабочей группы 64)
                GL.DispatchCompute((editsToApply + 63) / 64, 1, 1);

                // ОБЯЗАТЕЛЬНО: Ждем, пока GPU закончит менять байты и маски, прежде чем рисовать кадр
                GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
            }
        }

        // =========================================================
        // 2. ЗАГРУЖАЕМ ЦЕЛЫЕ ЧАНКИ (если они были сгенерированы)
        // =========================================================
        int limit = GameSettings.GpuUploadSpeed;
        while (limit > 0 && _uploadQueue.TryDequeue(out var chunk))
        {
            if (_allocatedChunks.TryGetValue(chunk.Position, out int slot))
            {
                lock (_chunksPendingUpload) _chunksPendingUpload.Remove(chunk.Position);
                // Загружаем только если это НЕ сплошной uniform-чанк
                if (chunk.IsLoaded && slot != SOLITARY_SLOT_INDEX)
                {
                    UploadChunkVoxels(chunk, slot);
                    limit--;
                }
            }
        }

        // =========================================================
        // 3. ОБНОВЛЯЕМ PAGE TABLE (СЛОВАРЬ ЧАНКОВ)
        // =========================================================
        if (_pageTableDirty)
        {
            GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, PT_X, PT_Y, PT_Z, PixelFormat.RedInteger, PixelType.UnsignedInt, _cpuPageTable);
            _pageTableDirty = false;
        }

        // =========================================================
        // 4. ОБНОВЛЯЕМ ДИНАМИЧЕСКИЕ ОБЪЕКТЫ И СЕТКУ (TEARDOWN PHYSICS)
        // =========================================================
        UpdateDynamicObjectsAndGrid();
    }

    public void UnloadChunk(Vector3i pos) 
    { 
        if (_allocatedChunks.TryGetValue(pos, out int slot)) 
        { 
            if (slot != SOLITARY_SLOT_INDEX) _freeSlots.Enqueue(slot); 
            _allocatedChunks.Remove(pos); 
            _cpuPageTable[GetPageTableIndex(pos)] = 0xFFFFFFFF; 
            _pageTableDirty = true; 
            lock(_chunksPendingUpload) { _chunksPendingUpload.Remove(pos); } 
        } 
    }

    private void CreateAuxBuffers() { 
        if (_gridHeadTexture == 0) { _gridHeadTexture = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture3D, _gridHeadTexture); GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32i, OBJ_GRID_SIZE, OBJ_GRID_SIZE, OBJ_GRID_SIZE, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero); GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest); GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest); }
        if (_linkedListSsbo == 0) { _linkedListSsbo = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _linkedListSsbo); GL.BufferData(BufferTarget.ShaderStorageBuffer, 2*1024*1024*8, IntPtr.Zero, BufferUsageHint.DynamicDraw); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo); }
        if (_atomicCounterBuffer == 0) { _atomicCounterBuffer = GL.GenBuffer(); GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer); GL.BufferData(BufferTarget.AtomicCounterBuffer, 4, IntPtr.Zero, BufferUsageHint.DynamicDraw); GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, _atomicCounterBuffer); }
        if (_dynamicObjectsBuffer == 0) { _dynamicObjectsBuffer = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer); GL.BufferData(BufferTarget.ShaderStorageBuffer, 4096 * Marshal.SizeOf<GpuDynamicObject>(), IntPtr.Zero, BufferUsageHint.DynamicDraw); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer); }
        if (_beamFbo == 0) _beamFbo = GL.GenFramebuffer(); 
        if (_beamTexture == 0) { _beamTexture = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, _beamTexture); GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R32f, _beamWidth, _beamHeight, 0, PixelFormat.Red, PixelType.Float, IntPtr.Zero); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest); }
        // НОВОЕ: Буфер правок (Binding 6)
        if (_editSsbo == 0)
        {
            _editSsbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _editSsbo);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, 2048 * 16, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, _editSsbo);
        }

    }

    private void CleanupBuffers() { 
        GL.UseProgram(0); GL.BindVertexArray(0); GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); 
        for(int i=0; i<32; i++) GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, i, 0); 
        GL.Flush(); GL.Finish(); 
        CleanupBanks(); 
        if (_maskSsbo != 0) { GL.DeleteBuffer(_maskSsbo); _maskSsbo = 0; } 
        if (_pageTableTexture != 0) { GL.DeleteTexture(_pageTableTexture); _pageTableTexture = 0; } 
        CurrentAllocatedBytes = 0; // Убрал _currentCapacity
        System.Threading.Thread.Sleep(50); 
    }
    
    private void UploadChunkVoxels(Chunk chunk, int globalSlot) 
    { 
        chunk.ReadDataUnsafe((srcVoxels, srcMasks) => { 
            if (srcVoxels == null) return; 
        
            // --- ИСПРАВЛЕНИЕ: ЛИНЕЙНАЯ АДРЕСАЦИЯ ---
            // Было: int bankIndex = globalSlot % _activeBankCount; (ОШИБКА)
            // Стало:
            int bankIndex = globalSlot / _chunksPerBank; 
            int localSlot = globalSlot % _chunksPerBank; 
            // ---------------------------------------

            // Защита от выхода за пределы (если слотов выделено больше, чем банков создано)
            if (bankIndex >= MAX_TOTAL_BANKS || _voxelSsboBanks[bankIndex] == 0) 
            {
                // Тут можно логировать ошибку или вызвать TryAddBank(), 
                // но по логике GetSlotOrPanic банк уже должен быть.
                return; 
            }

            System.Buffer.BlockCopy(srcVoxels, 0, _chunkUploadBuffer, 0, ChunkVol); 
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsboBanks[bankIndex]); 
        
            // Пишем по смещению
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)((long)localSlot * PackedChunkSizeInInts * 4), PackedChunkSizeInInts * 4, _chunkUploadBuffer); 
        
            if (srcMasks != null) { 
                System.Buffer.BlockCopy(srcMasks, 0, _maskUploadBuffer, 0, ChunkMaskSizeInUlongs * 8); 
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _maskSsbo); 
                GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)((long)globalSlot * ChunkMaskSizeInUlongs * 8), ChunkMaskSizeInUlongs * 8, _maskUploadBuffer); 
            } 
        }); 
    }

    public void NotifyVoxelEdited(Chunk chunk, Vector3i localPos, MaterialType newMat)
    {
        if (_allocatedChunks.TryGetValue(chunk.Position, out int slot))
        {
            if (slot == SOLITARY_SLOT_INDEX)
            {
                // Если чанк был сплошным воздухом/камнем, у него нет физического слота в SSBO.
                // Тут дельта не спасет, грузим целиком (происходит очень редко).
                NotifyChunkLoaded(chunk);
            }
            else
            {
                // Кладем правку в очередь для Compute Shader
                int index = localPos.X + Constants.ChunkResolution * (localPos.Y + Constants.ChunkResolution * localPos.Z);
                _editQueue.Enqueue(new GpuVoxelEdit
                {
                    ChunkSlot = (uint)slot,
                    VoxelIndex = (uint)index,
                    NewMaterial = (uint)newMat,
                    Padding = 0
                });
            }
        }
    }
    public void UpdateDynamicObjectsAndGrid() 
    { 
        var voxelObjects = _worldManager.GetAllVoxelObjects(); 
        
        // === ВНЕДРЕНИЕ ВЬЮМОДЕЛИ ===
        // Создаем временный список, включающий обычные объекты + вьюмодель
        int totalCount = voxelObjects.Count;
        if (_currentViewModel != null) totalCount++;

        // Ресайз буфера если надо
        if (totalCount > _tempGpuObjectsArray.Length) 
        { 
            Array.Resize(ref _tempGpuObjectsArray, totalCount + 1024); 
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer); 
            GL.BufferData(BufferTarget.ShaderStorageBuffer, _tempGpuObjectsArray.Length * Marshal.SizeOf<GpuDynamicObject>(), IntPtr.Zero, BufferUsageHint.DynamicDraw); 
        } 
        
        // Заполняем буфер
        int currentIndex = 0;

        // 1. Обычные объекты
        float alpha = _worldManager.PhysicsWorld.PhysicsAlpha;
        for (int i = 0; i < voxelObjects.Count; i++) 
        { 
            var vo = voxelObjects[i]; 
            FillGpuObject(ref _tempGpuObjectsArray[currentIndex], vo, alpha, false);
            currentIndex++;
        } 

        // 2. Вьюмодель (всегда последняя, или как удобно)
        if (_currentViewModel != null)
        {
            // Для вьюмодели alpha = 1.0, так как мы обновляем её позицию каждый кадр в Update, без физики
            FillGpuObject(ref _tempGpuObjectsArray[currentIndex], _currentViewModel, 1.0f, true);
            currentIndex++;
        }

        if (totalCount > 0) 
        { 
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer); 
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, totalCount * Marshal.SizeOf<GpuDynamicObject>(), _tempGpuObjectsArray); 
        } 
        
        // ... (Остальной код очистки сетки и вызова компутера остается тем же) ...
        // ВНИМАНИЕ: uObjectCount теперь равен totalCount!
        
        // [КОПИРУЕМ СТАРУЮ ЛОГИКУ СЕТКИ, НО МЕНЯЕМ count НА totalCount]
        float snap = _gridCellSize; 
        Vector3 playerPos = _worldManager.GetPlayerPosition(); 
        Vector3 snappedCenter = new Vector3((float)Math.Floor(playerPos.X / snap) * snap, (float)Math.Floor(playerPos.Y / snap) * snap, (float)Math.Floor(playerPos.Z / snap) * snap); 
        float halfExtent = (OBJ_GRID_SIZE * _gridCellSize) / 2.0f; 
        _lastGridOrigin = snappedCenter - new Vector3(halfExtent); 
        uint zero = 0; 
        GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer); 
        GL.BufferSubData(BufferTarget.AtomicCounterBuffer, IntPtr.Zero, sizeof(uint), ref zero); 
        GL.ClearTexImage(_gridHeadTexture, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero); 
        
        if (totalCount > 0) { // <--- ТУТ totalCount
            _gridComputeShader.Use(); 
            _gridComputeShader.SetInt("uObjectCount", totalCount); // <--- И ТУТ
            _gridComputeShader.SetVector3("uGridOrigin", _lastGridOrigin); 
            _gridComputeShader.SetFloat("uGridStep", _gridCellSize); 
            _gridComputeShader.SetInt("uGridSize", OBJ_GRID_SIZE); 
            GL.BindImageTexture(1, _gridHeadTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32i); 
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer); 
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo); 
            GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, _atomicCounterBuffer); 
            GL.DispatchCompute((totalCount + 63) / 64, 1, 1); // <--- И ТУТ
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.AtomicCounterBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit); 
        } 
    }
    
    // Вспомогательный метод для заполнения структуры
    private void FillGpuObject(ref GpuDynamicObject gpuObj, VoxelObject vo, float alpha, bool isViewModel)
    {
        Matrix4 model;
        if (isViewModel)
        {
            // Берем масштаб из самого объекта!
            float scale = vo.Scale;
     
            Vector3 centeringPivot = new Vector3(-1.0f * Constants.VoxelSize, 0.0f, -1.0f * Constants.VoxelSize);
     
            model = Matrix4.CreateTranslation(centeringPivot) * 
                    Matrix4.CreateScale(scale) *
                    Matrix4.CreateFromQuaternion(vo.Rotation) *
                    Matrix4.CreateTranslation(vo.Position);
        }
        else
        {
            model = vo.GetInterpolatedModelMatrix(alpha);
        }
        
        var col = MaterialRegistry.GetColor(vo.Material); 
        gpuObj.Model = model; 
        gpuObj.InvModel = Matrix4.Invert(model); 
        gpuObj.Color = new Vector4(col.r, col.g, col.b, 1.0f); 
        gpuObj.BoxMin = new Vector4(vo.LocalBoundsMin, 0); 
        gpuObj.BoxMax = new Vector4(vo.LocalBoundsMax, 0); 
    }

    public void Render(Camera cam)
    {
        if (_reallocationPending || _activeBankCount == 0) return;

        // --- 1. ПОДГОТОВКА МАТРИЦ ---
        _frameIndex++;

        // Расчет джиттера (Halton)
        int index = (int)(_frameIndex % 8);
        Vector2 halton = _haltonSequence[index]; // 0..1

        // Смещаем к центру (-0.5..0.5) и УМЕНЬШАЕМ АМПЛИТУДУ (масштабируем на 0.2)
        // Это уберет видимую "тряску" камеры, но оставит микро-сдвиги для антиалиасинга
        Vector2 jitter = (halton - new Vector2(0.5f)) * 0.1f;

        Matrix4 view = cam.GetViewMatrix();
        Matrix4 cleanProj = cam.GetProjectionMatrix();
        Matrix4 cleanViewProj = view * cleanProj; // Чистая матрица (без джиттера)

        // Если TAA выключен или это первый кадр - джиттер равен 0
        bool useTaa = GameSettings.EnableTAA;
        if (_resetTaaHistory) useTaa = false;

        Matrix4 activeProj = useTaa ? cam.GetJitteredProjectionMatrix(jitter, _renderWidth, _renderHeight) : cleanProj;
        Matrix4 activeViewProj = view * activeProj;

        // --- 2. РЕЙКАСТИНГ ---
        _shaderSystem.Use();
        var shader = _shaderSystem.RaycastShader;
        if (shader == null) return;

        shader.SetVector3("uCamPos", cam.Position);
        shader.SetMatrix4("uView", view);

        // Вернули как было: просто uProjection получает активную (с джиттером) матрицу
        shader.SetMatrix4("uProjection", activeProj);
        shader.SetMatrix4("uInvProjection", Matrix4.Invert(activeProj));
        shader.SetMatrix4("uInvView", Matrix4.Invert(view));

        // ... (остальные параметры uRenderDistance, uTime и т.д. не трогаем, они не менялись) ...
        float viewRange = _worldManager.GetViewRangeInMeters();
        shader.SetFloat("uRenderDistance", viewRange);
        shader.SetFloat("uLodDistance", GameSettings.EnableLOD ? viewRange * GameSettings.LodPercentage : 100000.0f);
        shader.SetInt("uDisableEffectsOnLOD", GameSettings.DisableEffectsOnLOD ? 1 : 0);
        shader.SetInt("uMaxRaySteps", (int)(GameSettings.RenderDistance * 8) + 2028);
        
        // --- РАСЧЕТ НЕБЕСНЫХ ТЕЛ ---
        // 1. Орбита Солнца (полный круг каждые 24 часа)
        float sunAngle = ((float)GameSettings.TotalTimeHours - 6.0f) / 24.0f * MathHelper.TwoPi;
        float sunX = (float)Math.Cos(sunAngle);
        float sunY = (float)Math.Sin(sunAngle);
        float sunZ = 0.3f; // Наклон орбиты солнца

        // 2. Орбита Луны (Лунный цикл = 28 дней. Луна отстает от солнца каждый день)
        float moonPhaseOffset = ((float)GameSettings.TotalTimeHours / (24.0f * 28.0f)) * MathHelper.TwoPi;
        // -Pi нужно чтобы в нулевой день было Полнолуние
        float moonAngle = sunAngle - MathHelper.Pi - moonPhaseOffset; 
        
        float moonX = (float)Math.Cos(moonAngle);
        float moonY = (float)Math.Sin(moonAngle);
        float moonZ = -0.1f; // Луна имеет немного другой наклон орбиты

        Vector3 dynamicSunDir = Vector3.Normalize(new Vector3(sunX, sunY, sunZ));
        Vector3 dynamicMoonDir = Vector3.Normalize(new Vector3(moonX, moonY, moonZ));

        shader.SetVector3("uSunDir", dynamicSunDir);
        shader.SetVector3("uMoonDir", dynamicMoonDir); // Передаем Луну в шейдер!

        Vector3 p = cam.Position;
        int sz = Constants.ChunkSizeWorld;
        int cx = (int)Math.Floor(p.X / sz), cy = (int)Math.Floor(p.Y / sz), cz = (int)Math.Floor(p.Z / sz);
        int r = GameSettings.RenderDistance + 2;
        shader.SetInt("uBoundMinX", cx - r); shader.SetInt("uBoundMinY", 0); shader.SetInt("uBoundMinZ", cz - r);
        shader.SetInt("uBoundMaxX", cx + r); shader.SetInt("uBoundMaxY", WorldManager.WorldHeightChunks); shader.SetInt("uBoundMaxZ", cz + r);

        GL.BindImageTexture(0, _pageTableTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32ui);
        BindAllBuffers();
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo);
        GL.BindImageTexture(1, _gridHeadTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32i);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo);
        shader.SetTexture("uNoiseTexture", _noiseTexture, TextureUnit.Texture0);
        shader.SetVector3("uGridOrigin", _lastGridOrigin);
        shader.SetFloat("uGridStep", _gridCellSize);
        shader.SetInt("uGridSize", OBJ_GRID_SIZE);
        shader.SetInt("uObjectCount", _worldManager.GetAllVoxelObjects().Count + (_currentViewModel != null ? 1 : 0));

        GL.BindVertexArray(_quadVao);

        if (GameSettings.BeamOptimization)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _beamFbo);
            GL.Viewport(0, 0, _beamWidth, _beamHeight);
            GL.ClearColor(10000.0f, 0, 0, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            shader.SetInt("uIsBeamPass", 1);
            GL.Disable(EnableCap.DepthTest);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }

        // Main Render
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _mainFbo);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0.5f, 0.7f, 0.9f, 1.0f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        shader.SetInt("uIsBeamPass", 0);
        shader.SetTexture("uBeamTexture", _beamTexture, TextureUnit.Texture1);
        GL.Enable(EnableCap.DepthTest);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        // --- 3. TAA RESOLVE (СТАРЫЙ ВАРИАНТ) ---
        if (useTaa && !_resetTaaHistory)
        {
            int readIndex = 1 - _historyWriteIndex;
            int writeIndex = _historyWriteIndex;

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _historyFbo[writeIndex]);
            GL.Viewport(0, 0, _renderWidth, _renderHeight);
            GL.Disable(EnableCap.DepthTest);

            _taaShader.Use();
            Vector2 currJitterNDC = useTaa
            ? new Vector2((jitter.X / _renderWidth) * 2.0f, (jitter.Y / _renderHeight) * 2.0f)
            : Vector2.Zero;

            _taaShader.SetVector2("uCurrentJitterNDC", currJitterNDC);
            _taaShader.SetVector2("uPrevJitterNDC", _prevJitterNDC);
            // Эти uniform'ы были в оригинале, я их не трогаю, но добавлю обратно старые матрицы
            _taaShader.SetTexture("uCurrentColorTexture", _mainColorTexture, TextureUnit.Texture0);
            _taaShader.SetTexture("uCurrentDepthTexture", _mainDepthTexture, TextureUnit.Texture1);
            _taaShader.SetTexture("uHistoryTexture", _historyTexture[readIndex], TextureUnit.Texture2);

            _taaShader.SetMatrix4("uInvViewProj", Matrix4.Invert(cleanViewProj));
            _taaShader.SetMatrix4("uPrevViewProj", _prevCleanViewProjection);

            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _historyFbo[writeIndex]);
        }
        else
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _mainFbo);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _historyFbo[_historyWriteIndex]);
            GL.BlitFramebuffer(0, 0, _renderWidth, _renderHeight, 0, 0, _renderWidth, _renderHeight,
                               ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _historyFbo[1 - _historyWriteIndex]);
            GL.BlitFramebuffer(0, 0, _renderWidth, _renderHeight, 0, 0, _renderWidth, _renderHeight,
                               ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _mainFbo);
            if (GameSettings.EnableTAA) _resetTaaHistory = false;
        }

        // --- 4. UPSCALE ---
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        GL.Viewport(0, 0, _windowWidth, _windowHeight);

        // Blit цвета 
        GL.BlitFramebuffer(0, 0, _renderWidth, _renderHeight,
                           0, 0, _windowWidth, _windowHeight,
                           ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

        // Blit глубины 
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _mainFbo);
        GL.BlitFramebuffer(0, 0, _renderWidth, _renderHeight, 0, 0, _windowWidth, _windowHeight, ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.BindVertexArray(0);

        _prevCleanViewProjection = cleanViewProj;
        _prevJitterNDC = useTaa
            ? new Vector2((jitter.X / _renderWidth) * 2.0f, (jitter.Y / _renderHeight) * 2.0f)
            : Vector2.Zero;
        _historyWriteIndex = 1 - _historyWriteIndex;
    }

    public void OnResize(int w, int h)
    {
        _windowWidth = w; _windowHeight = h;
        _renderWidth = Math.Max(1, (int)(w * GameSettings.RenderScale));
        _renderHeight = Math.Max(1, (int)(h * GameSettings.RenderScale));
        _beamWidth = _renderWidth / BeamDivisor;
        _beamHeight = _renderHeight / BeamDivisor;
        if (_beamWidth < 1) _beamWidth = 1; if (_beamHeight < 1) _beamHeight = 1;
        if (_beamTexture != 0) GL.DeleteTexture(_beamTexture);
        _beamTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _beamTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R32f,
                      _beamWidth, _beamHeight, 0, PixelFormat.Red, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _beamFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                                TextureTarget.Texture2D, _beamTexture, 0);

        // Основной FBO для рейкастинга
        if (_mainFbo != 0) GL.DeleteFramebuffer(_mainFbo);
        if (_mainColorTexture != 0) GL.DeleteTexture(_mainColorTexture);
        if (_mainDepthTexture != 0) GL.DeleteTexture(_mainDepthTexture);
        _mainFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _mainFbo);
        _mainColorTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _mainColorTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, _renderWidth, _renderHeight, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _mainColorTexture, 0);
        _mainDepthTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _mainDepthTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, _renderWidth, _renderHeight, 0, PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _mainDepthTexture, 0);

        // Два FBO для пинг-понга истории TAA
        for (int i = 0; i < 2; i++)
        {
            if (_historyFbo[i] != 0) GL.DeleteFramebuffer(_historyFbo[i]);
            if (_historyTexture[i] != 0) GL.DeleteTexture(_historyTexture[i]);
            _historyFbo[i] = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _historyFbo[i]);
            _historyTexture[i] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _historyTexture[i]);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, _renderWidth, _renderHeight, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _historyTexture[i], 0);
            GL.ClearColor(0, 0, 0, 0);
            GL.Clear(ClearBufferMask.ColorBufferBit);
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, _windowWidth, _windowHeight);
        // Сбрасываем историю при ресайзе
        _resetTaaHistory = true;
        _frameIndex = 0;
    }
    public void ReloadShader() { _shaderSystem.Compile(MAX_TOTAL_BANKS, _chunksPerBank); }
    public void UploadAllVisibleChunks() { foreach (var c in _worldManager.GetChunksSnapshot()) NotifyChunkLoaded(c); }
    public void Dispose() { CleanupBuffers(); _quadVao = 0; _shaderSystem?.Dispose(); _gridComputeShader?.Dispose(); }
    private int LoadTexture(string path) { if (!File.Exists(path)) return 0; int handle = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, handle); StbImage.stbi_set_flip_vertically_on_load(1); using (Stream stream = File.OpenRead(path)) { ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha); GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, image.Width, image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, image.Data); } GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear); GL.GenerateMipmap(GenerateMipmapTarget.Texture2D); return handle; }
    private int GetPageTableIndex(Vector3i p) => (p.X & MASK_X) + PT_X * ((p.Y & MASK_Y) + PT_Y * (p.Z & MASK_Z));
    // дебаг
    public string GetMemoryDebugInfo()
    {
        int totalSlots = _activeBankCount * _chunksPerBank;
        int usedSlots = totalSlots - _freeSlots.Count;
        float percent = (float)usedSlots / totalSlots * 100f;
    
        return $"VRAM Banks: {_activeBankCount} | Slots: {usedSlots}/{totalSlots} ({percent:F1}%) | LoadedChunks: {_allocatedChunks.Count}";
    }
}