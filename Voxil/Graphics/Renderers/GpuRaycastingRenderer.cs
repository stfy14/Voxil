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
    private const int MAX_TOTAL_BANKS = 13; 
    private const long BANK_SIZE_BYTES = 1L * 1024L * 1024L * 1024L - 1; // 2 ГБ минус 1 байт
    
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
    private const int OBJ_GRID_SIZE = 512;
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
    
    public void RequestReallocation() { CleanupBuffers(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); _reallocationPending = true; }
    public bool IsReallocationPending() => _reallocationPending;
    public void PerformReallocation() { if (!_reallocationPending) return; InitializeBuffers(); _reallocationPending = false; }

    public long CalculateMemoryBytesForDistance(int distance)
    {
        int chunks = (distance * 2 + 1) * (distance * 2 + 1) * WorldManager.WorldHeightChunks;
        return (long)chunks * (PackedChunkSizeInInts * 4 + ChunkMaskSizeInUlongs * 8);
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
        if (!TryAddBank()) throw new Exception("Critical: GPU has less than 1GB VRAM available!");
        _chunksPerBank = (int)(BANK_SIZE_BYTES / (PackedChunkSizeInInts * 4));

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
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10 + i, handle);
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
        int availableKb = GetAvailableVramKb();
        long neededKb = BANK_SIZE_BYTES / 1024;
        if (availableKb < neededKb + 500000) return false; 

        try
        {
            int newIndex = _activeBankCount;
            int newBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, newBuffer);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)BANK_SIZE_BYTES, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            
            _voxelSsboBanks[newIndex] = newBuffer;
            _activeBankCount++;
            CurrentAllocatedBytes += BANK_SIZE_BYTES;
            
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10 + newIndex, newBuffer);
            Console.WriteLine($"[VRAM] Added Bank #{newIndex}. Total: {_activeBankCount}");
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
        int limit = GameSettings.GpuUploadSpeed; 
        while (limit > 0 && _uploadQueue.TryDequeue(out var chunk)) 
        { 
            if (_allocatedChunks.TryGetValue(chunk.Position, out int slot)) 
            { 
                lock(_chunksPendingUpload) _chunksPendingUpload.Remove(chunk.Position);
                // Загружаем только если это НЕ solid-чанк
                if (chunk.IsLoaded && slot != SOLITARY_SLOT_INDEX) 
                { 
                    UploadChunkVoxels(chunk, slot); 
                    limit--; 
                }
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
    
    public void UpdateDynamicObjectsAndGrid() 
    { 
        var voxelObjects = _worldManager.GetAllVoxelObjects(); 
        int count = voxelObjects.Count; 
        
        if (count > _tempGpuObjectsArray.Length) 
        { 
            Array.Resize(ref _tempGpuObjectsArray, count + 1024); 
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer); 
            GL.BufferData(BufferTarget.ShaderStorageBuffer, _tempGpuObjectsArray.Length * Marshal.SizeOf<GpuDynamicObject>(), IntPtr.Zero, BufferUsageHint.DynamicDraw); 
        } 
        
        if (count > 0) 
        { 
            // Получаем Alpha из физического мира
            float alpha = _worldManager.PhysicsWorld.PhysicsAlpha;

            for (int i = 0; i < count; i++) 
            { 
                var vo = voxelObjects[i]; 
                
                // ИСПОЛЬЗУЕМ ИНТЕРПОЛЯЦИЮ
                Matrix4 model = vo.GetInterpolatedModelMatrix(alpha);
                
                var col = MaterialRegistry.GetColor(vo.Material); 
                _tempGpuObjectsArray[i].Model = model; 
                _tempGpuObjectsArray[i].InvModel = Matrix4.Invert(model); 
                _tempGpuObjectsArray[i].Color = new Vector4(col.r, col.g, col.b, 1.0f); 
                _tempGpuObjectsArray[i].BoxMin = new Vector4(vo.LocalBoundsMin, 0); 
                _tempGpuObjectsArray[i].BoxMax = new Vector4(vo.LocalBoundsMax, 0); 
            } 
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer); 
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, count * Marshal.SizeOf<GpuDynamicObject>(), _tempGpuObjectsArray); 
        } 
        
        // ... (остальной код метода без изменений: расчет сетки, запуск компута и т.д.) ...
        float snap = _gridCellSize; 
        Vector3 playerPos = _worldManager.GetPlayerPosition(); 
        Vector3 snappedCenter = new Vector3((float)Math.Floor(playerPos.X / snap) * snap, (float)Math.Floor(playerPos.Y / snap) * snap, (float)Math.Floor(playerPos.Z / snap) * snap); 
        float halfExtent = (OBJ_GRID_SIZE * _gridCellSize) / 2.0f; 
        _lastGridOrigin = snappedCenter - new Vector3(halfExtent); 
        uint zero = 0; 
        GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer); 
        GL.BufferSubData(BufferTarget.AtomicCounterBuffer, IntPtr.Zero, sizeof(uint), ref zero); 
        GL.ClearTexImage(_gridHeadTexture, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero); 
        if (count > 0) { 
            _gridComputeShader.Use(); 
            _gridComputeShader.SetInt("uObjectCount", count); 
            _gridComputeShader.SetVector3("uGridOrigin", _lastGridOrigin); 
            _gridComputeShader.SetFloat("uGridStep", _gridCellSize); 
            _gridComputeShader.SetInt("uGridSize", OBJ_GRID_SIZE); 
            GL.BindImageTexture(1, _gridHeadTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32i); 
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer); 
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo); 
            GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, _atomicCounterBuffer); 
            GL.DispatchCompute((count + 63) / 64, 1, 1); 
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.AtomicCounterBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit); 
        } 
    }
    
    public void Render(Camera cam) 
    { 
        if (_reallocationPending || _activeBankCount == 0) return; 
        
        _shaderSystem.Use(); 
        var shader = _shaderSystem.RaycastShader; 
        if (shader == null) return; 
        
        // ... (передача uniforms без изменений) ...
        shader.SetVector3("uCamPos", cam.Position); 
        shader.SetMatrix4("uView", cam.GetViewMatrix()); 
        shader.SetMatrix4("uProjection", cam.GetProjectionMatrix()); 
        shader.SetMatrix4("uInvView", Matrix4.Invert(cam.GetViewMatrix())); 
        shader.SetMatrix4("uInvProjection", Matrix4.Invert(cam.GetProjectionMatrix())); 
        float viewRange = _worldManager.GetViewRangeInMeters(); 
        shader.SetFloat("uRenderDistance", viewRange); 
        
        if (GameSettings.EnableLOD) { 
            float lodMeters = viewRange * GameSettings.LodPercentage; 
            shader.SetFloat("uLodDistance", lodMeters); 
        } else { 
            shader.SetFloat("uLodDistance", 100000.0f); 
        } 
        
        shader.SetInt("uDisableEffectsOnLOD", GameSettings.DisableEffectsOnLOD ? 1 : 0); 
        shader.SetInt("uMaxRaySteps", (int)(GameSettings.RenderDistance * 8) + 2028); 
        shader.SetVector3("uSunDir", Vector3.Normalize(new Vector3(0.2f, 0.4f, 0.8f))); 
        shader.SetFloat("uTime", _totalTime); 
        shader.SetInt("uShowDebugHeatmap", GameSettings.ShowDebugHeatmap ? 1 : 0); 
        shader.SetInt("uSoftShadowSamples", GameSettings.SoftShadowSamples); 
        
        Vector3 p = cam.Position; 
        int sz = Constants.ChunkSizeWorld; 
        int cx = (int)Math.Floor(p.X/sz), cy = (int)Math.Floor(p.Y/sz), cz = (int)Math.Floor(p.Z/sz); 
        int r = GameSettings.RenderDistance + 2; 
        
        shader.SetInt("uBoundMinX", cx-r); shader.SetInt("uBoundMinY", 0); shader.SetInt("uBoundMinZ", cz-r); 
        shader.SetInt("uBoundMaxX", cx+r); shader.SetInt("uBoundMaxY", WorldManager.WorldHeightChunks); shader.SetInt("uBoundMaxZ", cz+r); 
        
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
        shader.SetInt("uObjectCount", _worldManager.GetAllVoxelObjects().Count); 
        
        GL.BindVertexArray(_quadVao); 
        
        if (GameSettings.BeamOptimization) 
        { 
            if (_beamTexture == 0) { 
                _beamTexture = GL.GenTexture(); 
                GL.BindTexture(TextureTarget.Texture2D, _beamTexture); 
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R32f, _beamWidth, _beamHeight, 0, PixelFormat.Red, PixelType.Float, IntPtr.Zero); 
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest); 
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
                // ИСПРАВЛЕНИЕ 1: Убираем артефакты по краям экрана
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            } 
            
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _beamFbo); 
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _beamTexture, 0); 
            GL.Viewport(0, 0, _beamWidth, _beamHeight); 
            
            // Ставим красный цвет для очистки глубины
            GL.ClearColor(10000.0f, 0, 0, 1); 
            GL.Clear(ClearBufferMask.ColorBufferBit); 
            
            shader.SetInt("uIsBeamPass", 1); 
            GL.Disable(EnableCap.DepthTest); 
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            
            // ИСПРАВЛЕНИЕ 2: Возвращаем нормальный цвет очистки (иначе в след. кадре небо будет красным)
            // Цвет как в Game.cs
            GL.ClearColor(0.5f, 0.7f, 0.9f, 1.0f); 
        } 
        
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); 
        GL.Viewport(0, 0, _windowWidth, _windowHeight); 
        shader.SetInt("uIsBeamPass", 0); 
        shader.SetTexture("uBeamTexture", _beamTexture, TextureUnit.Texture1); 
        
        // Включаем DepthTest перед основным проходом
        GL.Enable(EnableCap.DepthTest); 
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4); 
        GL.BindVertexArray(0); 
    }
    
    public void OnResize(int w, int h) { _windowWidth = w; _windowHeight = h; _beamWidth = w / BeamDivisor; _beamHeight = h / BeamDivisor; if (_beamWidth < 1) _beamWidth = 1; if (_beamHeight < 1) _beamHeight = 1; if (_beamTexture != 0) { GL.DeleteTexture(_beamTexture); _beamTexture = 0; } GL.Viewport(0, 0, _windowWidth, _windowHeight); }
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