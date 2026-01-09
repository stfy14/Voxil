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
    private Shader _shader;
    private Shader _gridComputeShader;
    private int _quadVao;
    private readonly WorldManager _worldManager;

    private int _pageTableTexture;
    private int _voxelSsbo;
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

    private const int ChunkMaskSizeInUlongs = Chunk.MasksCount;
    private readonly ulong[] _maskUploadBuffer = new ulong[ChunkMaskSizeInUlongs];
    private const int ChunkVol = Constants.ChunkVolume;
    private const int PackedChunkSizeInInts = ChunkVol / 4;
    private readonly uint[] _chunkUploadBuffer = new uint[PackedChunkSizeInInts];

    private const int PT_X = 512; private const int PT_Y = 16; private const int PT_Z = 512;
    private const int MASK_X = PT_X - 1; private const int MASK_Y = PT_Y - 1; private const int MASK_Z = PT_Z - 1;
    private const int OBJ_GRID_SIZE = 256;
    private float _gridCellSize;

    private Stack<int> _freeSlots = new Stack<int>();
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
        _gridCellSize = Math.Max(Constants.VoxelSize, 0.5f);
        TotalVramMb = DetectTotalVram();
    }

    public void Load()
    {
        ReloadShader();
        _gridComputeShader = new Shader("Shaders/grid_update.comp");
        
        _quadVao = GL.GenVertexArray(); 
        GL.BindVertexArray(_quadVao);
        int dummyVbo = GL.GenBuffer(); 
        GL.BindBuffer(BufferTarget.ArrayBuffer, dummyVbo);
        float[] dummyData = new float[8] { -1, -1, 1, -1, -1, 1, 1, 1 };
        GL.BufferData(BufferTarget.ArrayBuffer, sizeof(float) * 8, dummyData, BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0); 
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        _noiseTexture = LoadTexture("Shaders/Images/water_noise.png");

        InitializeBuffers();
        UploadAllVisibleChunks();
    }

    private int DetectTotalVram()
    {
        try {
            const int GPU_MEMORY_INFO_TOTAL_AVAILABLE_MEMORY_NVX = 0x9048;
            GL.GetInteger((GetPName)GPU_MEMORY_INFO_TOTAL_AVAILABLE_MEMORY_NVX, out int totalKb);
            if (totalKb > 0) return totalKb / 1024;
            
            const int VBO_FREE_MEMORY_ATI = 0x87FB;
            int[] param = new int[4];
            GL.GetInteger((GetPName)VBO_FREE_MEMORY_ATI, param);
            if (param[0] > 0) return param[0] / 1024;
        } catch {}
        return 4096;
    }
    
    // НОВЫЙ МЕТОД: Запускает процесс очистки
    public void RequestReallocation()
    {
        Console.WriteLine("[Renderer] Reallocation requested. Cleaning up old buffers...");
        CleanupBuffers(); // Удаляем старые буферы
    
        // КРИТИЧЕСКИ ВАЖНО: Принудительная очистка памяти
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    
        Console.WriteLine("[Renderer] Garbage collection completed. Waiting for GPU driver...");
        _reallocationPending = true;
        _currentCapacity = 0;
    }

    // НОВЫЙ МЕТОД: Возвращает состояние флага
    public bool IsReallocationPending() => _reallocationPending;
    
    public void PerformReallocation()
    {
        if (!_reallocationPending) return;

        Console.WriteLine("[Renderer] Performing reallocation of new buffers...");
        InitializeBuffers(); // Создаем новые буферы с правильным размером
        ReloadShader();
        _reallocationPending = false;
    
        Console.WriteLine($"[Renderer] Reallocation complete. New capacity: {_currentCapacity}");
    }

    public long CalculateMemoryBytesForDistance(int distance)
    {
        int chunkCount = (distance * 2 + 1) * (distance * 2 + 1) * WorldManager.WorldHeightChunks;
        long bytesPerChunkVoxel = PackedChunkSizeInInts * 4;
        long bytesPerChunkMask = ChunkMaskSizeInUlongs * 8;
        return (long)chunkCount * (bytesPerChunkVoxel + bytesPerChunkMask);
    }

    // Вспомогательный метод для сброса ошибок
    private void ClearGlErrors()
    {
        while (GL.GetError() != ErrorCode.NoError) { }
    }

public void InitializeBuffers()
{
    // 1. Считаем нужный размер
    int dist = Math.Max(GameSettings.RenderDistance, 4);
    int maxDist = dist + 2;
    int requestedChunks = (maxDist * 2 + 1) * (maxDist * 2 + 1) * WorldManager.WorldHeightChunks;

    Console.WriteLine($"[Renderer] InitializeBuffers called. Requested: {requestedChunks}, Current: {_currentCapacity}");

    // 2. Если размер не изменился И буферы уже созданы, не делаем ничего
    if (_voxelSsbo != 0 && _currentCapacity == requestedChunks)
    {
        Console.WriteLine($"[Renderer] Buffer size matches. No reallocation needed.");
        return;
    }

    // 3. Расчет памяти
    long bytesPerChunkVoxel = PackedChunkSizeInInts * 4;
    long bytesPerChunkMask = ChunkMaskSizeInUlongs * 8;
    int finalCapacity = requestedChunks;
    
    long vBytes = (long)finalCapacity * bytesPerChunkVoxel;
    long mBytes = (long)finalCapacity * bytesPerChunkMask;
    long totalMB = (vBytes + mBytes) / 1024 / 1024;

    Console.WriteLine($"[Renderer] Target memory: {totalMB} MB ({finalCapacity} chunks)");

    // 4. Если хендлы еще не созданы (первый запуск)
    if (_voxelSsbo == 0)
    {
        Console.WriteLine("[Renderer] First-time allocation. Creating GPU objects...");
        _voxelSsbo = GL.GenBuffer();
        _maskSsbo = GL.GenBuffer();
        _pageTableTexture = GL.GenTexture();
        
        Console.WriteLine($"[Renderer] Created: VoxelSSBO={_voxelSsbo}, MaskSSBO={_maskSsbo}, PageTable={_pageTableTexture}");
        
        CreateAuxBuffers();
    }
    else
    {
        Console.WriteLine("[Renderer] Reusing existing GPU object handles.");
    }

    // 5. Обновляем capacity
    _currentCapacity = finalCapacity;
    CurrentAllocatedBytes = vBytes + mBytes;

    // 6. Переопределяем VOXEL SSBO
    Console.WriteLine($"[Renderer] Allocating Voxel SSBO: {(vBytes / 1024.0 / 1024.0):F2} MB");
    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsbo);
    GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)vBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
    
    // Проверка ошибок
    ErrorCode err = GL.GetError();
    if (err != ErrorCode.NoError)
    {
        Console.WriteLine($"[Renderer] ERROR allocating Voxel SSBO: {err}");
    }
    
    GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);

    // 7. Переопределяем MASK SSBO
    Console.WriteLine($"[Renderer] Allocating Mask SSBO: {(mBytes / 1024.0 / 1024.0):F2} MB");
    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _maskSsbo);
    GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)mBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
    
    err = GL.GetError();
    if (err != ErrorCode.NoError)
    {
        Console.WriteLine($"[Renderer] ERROR allocating Mask SSBO: {err}");
    }
    
    GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo);

    // 8. Переопределяем текстуру таблицы страниц
    Console.WriteLine("[Renderer] Updating PageTable texture...");
    GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
    GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32ui, PT_X, PT_Y, PT_Z, 0, 
                  PixelFormat.RedInteger, PixelType.UnsignedInt, _cpuPageTable);
    
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
    
    // 9. Flush для применения изменений
    GL.Flush();
    
    // 10. Сбрасываем CPU-состояние
    ResetAllocationLogic(_currentCapacity);
    _pageTableDirty = true;
    
    Console.WriteLine($"[Renderer] Buffer initialization complete. Final capacity: {_currentCapacity}");
}

    private void ResetAllocationLogic(int capacity)
    {
        _freeSlots.Clear();
        _allocatedChunks.Clear();
        for (int i = 0; i < capacity; i++) _freeSlots.Push(i);
        
        Array.Fill(_cpuPageTable, 0xFFFFFFFF);
        _chunksPendingUpload.Clear();
        while(!_uploadQueue.IsEmpty) _uploadQueue.TryDequeue(out _);
        _memoryFullWarned = false;
        _pageTableDirty = true;
    }

    private void CreateAuxBuffers()
    {
        if (_gridHeadTexture == 0) _gridHeadTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _gridHeadTexture);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32i, OBJ_GRID_SIZE, OBJ_GRID_SIZE, OBJ_GRID_SIZE, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);

        if (_linkedListSsbo == 0) _linkedListSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _linkedListSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, 2 * 1024 * 1024 * 8, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo);

        if (_atomicCounterBuffer == 0) _atomicCounterBuffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer);
        GL.BufferData(BufferTarget.AtomicCounterBuffer, sizeof(uint), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, _atomicCounterBuffer);

        if (_dynamicObjectsBuffer == 0) _dynamicObjectsBuffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, 4096 * Marshal.SizeOf<GpuDynamicObject>(), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);

        if (_beamFbo == 0) _beamFbo = GL.GenFramebuffer();
        if (_beamTexture == 0) _beamTexture = GL.GenTexture();
    }

    private void CleanupBuffers()
{
    Console.WriteLine("[Renderer] Starting buffer cleanup...");
    
    // 1. Сброс основного состояния OpenGL
    GL.UseProgram(0);
    GL.BindVertexArray(0);
    GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

    // 2. Отвязываем все текстуры от всех юнитов
    for (int i = 0; i < 16; i++) // 16 юнитов для надежности
    {
        GL.ActiveTexture(TextureUnit.Texture0 + i);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.BindTexture(TextureTarget.Texture3D, 0);
    }
    
    // 3. КРИТИЧЕСКИ ВАЖНО: Отвязываем все SSBO
    for (int i = 0; i < 8; i++)
    {
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, i, 0);
    }
    
    // 4. Отвязываем Atomic Counter Buffer
    GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, 0);
    
    // 5. Отвязываем Image Units
    for (int i = 0; i < 8; i++)
    {
        GL.BindImageTexture(i, 0, 0, false, 0, TextureAccess.ReadOnly, SizedInternalFormat.R8);
    }
    
    // 6. ДОПОЛНИТЕЛЬНО: Отвязываем буферы от таргетов
    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
    GL.BindBuffer(BufferTarget.AtomicCounterBuffer, 0);
    GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
    
    // 7. Сбрасываем текущую активную текстуру
    GL.ActiveTexture(TextureUnit.Texture0);
    
    // 8. ВАЖНО: Flush + Finish перед удалением
    GL.Flush();
    GL.Finish();
    
    Console.WriteLine("[Renderer] OpenGL state reset. Deleting GPU objects...");

    // 9. Теперь удаляем объекты (ОСНОВНЫЕ БУФЕРЫ)
    if (_pageTableTexture != 0)
    {
        Console.WriteLine($"[Renderer] Deleting PageTable texture (ID: {_pageTableTexture})");
        GL.DeleteTexture(_pageTableTexture);
        _pageTableTexture = 0;
    }
    
    if (_voxelSsbo != 0)
    {
        Console.WriteLine($"[Renderer] Deleting Voxel SSBO (ID: {_voxelSsbo})");
        GL.DeleteBuffer(_voxelSsbo);
        _voxelSsbo = 0;
    }
    
    if (_maskSsbo != 0)
    {
        Console.WriteLine($"[Renderer] Deleting Mask SSBO (ID: {_maskSsbo})");
        GL.DeleteBuffer(_maskSsbo);
        _maskSsbo = 0;
    }
    
    // 10. Удаляем вспомогательные буферы
    if (_gridHeadTexture != 0)
    {
        GL.DeleteTexture(_gridHeadTexture);
        _gridHeadTexture = 0;
    }
    if (_linkedListSsbo != 0)
    {
        GL.DeleteBuffer(_linkedListSsbo);
        _linkedListSsbo = 0;
    }
    if (_atomicCounterBuffer != 0)
    {
        GL.DeleteBuffer(_atomicCounterBuffer);
        _atomicCounterBuffer = 0;
    }
    if (_dynamicObjectsBuffer != 0)
    {
        GL.DeleteBuffer(_dynamicObjectsBuffer);
        _dynamicObjectsBuffer = 0;
    }
    if (_beamFbo != 0)
    {
        GL.DeleteFramebuffer(_beamFbo);
        _beamFbo = 0;
    }
    if (_beamTexture != 0)
    {
        GL.DeleteTexture(_beamTexture);
        _beamTexture = 0;
    }

    // 11. Финальный Flush + Finish
    GL.Flush();
    GL.Finish();
    
    // 12. Сбрасываем счетчики
    CurrentAllocatedBytes = 0;
    _currentCapacity = 0;
    
    Console.WriteLine("[Renderer] All GPU objects deleted. Waiting for driver to release memory...");
    
    // 13. КРИТИЧЕСКИ ВАЖНО: Даем драйверу время на освобождение
    System.Threading.Thread.Sleep(50); // 50ms задержка
}
    
    // ... (Standard methods follow) ...
    public void ResizeBuffers() { InitializeBuffers(); }
    public void UnloadChunk(Vector3i chunkPos) { if (_allocatedChunks.TryGetValue(chunkPos, out int slotIndex)) { _freeSlots.Push(slotIndex); _allocatedChunks.Remove(chunkPos); _cpuPageTable[GetPageTableIndex(chunkPos)] = 0xFFFFFFFF; _pageTableDirty = true; lock(_chunksPendingUpload) { _chunksPendingUpload.Remove(chunkPos); } if (_freeSlots.Count > 100) _memoryFullWarned = false; } }
    public void NotifyChunkLoaded(Chunk chunk) { if (chunk == null || !chunk.IsLoaded) return; if (chunk.SolidCount == 0) return; if (!_allocatedChunks.ContainsKey(chunk.Position)) { if (_freeSlots.Count > 0) { int slot = _freeSlots.Pop(); _allocatedChunks[chunk.Position] = slot; _cpuPageTable[GetPageTableIndex(chunk.Position)] = (uint)slot; _pageTableDirty = true; } else { if (!_memoryFullWarned) { Console.WriteLine($"[Renderer] GPU Memory FULL! Cannot upload chunk {chunk.Position}. Capacity: {_currentCapacity}"); _memoryFullWarned = true; } return; } } lock(_chunksPendingUpload) { if (_chunksPendingUpload.Add(chunk.Position)) { _uploadQueue.Enqueue(chunk); } } }
    public void Update(float deltaTime) { _totalTime += deltaTime; int uploadLimit = 2000; while (uploadLimit > 0 && _uploadQueue.TryDequeue(out var chunk)) { bool isStillAllocated = false; int slot = -1; if (_allocatedChunks.TryGetValue(chunk.Position, out slot)) { lock(_chunksPendingUpload) { if (_chunksPendingUpload.Contains(chunk.Position)) { _chunksPendingUpload.Remove(chunk.Position); isStillAllocated = true; } } } if (isStillAllocated && chunk.IsLoaded) { UploadChunkVoxels(chunk, slot); uploadLimit--; } } if (_pageTableDirty) { GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture); GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, PT_X, PT_Y, PT_Z, PixelFormat.RedInteger, PixelType.UnsignedInt, _cpuPageTable); _pageTableDirty = false; } UpdateDynamicObjectsAndGrid(); }
    private void UploadChunkVoxels(Chunk chunk, int offset) { long start = Stopwatch.GetTimestamp(); chunk.ReadDataUnsafe((srcVoxels, srcMasks) => { if (srcVoxels == null) return; System.Buffer.BlockCopy(srcVoxels, 0, _chunkUploadBuffer, 0, Constants.ChunkVolume); IntPtr gpuOffset = (IntPtr)((long)offset * PackedChunkSizeInInts * sizeof(uint)); GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsbo); GL.BufferSubData(BufferTarget.ShaderStorageBuffer, gpuOffset, PackedChunkSizeInInts * sizeof(uint), _chunkUploadBuffer); if (srcMasks != null) { System.Buffer.BlockCopy(srcMasks, 0, _maskUploadBuffer, 0, ChunkMaskSizeInUlongs * sizeof(ulong)); IntPtr maskGpuOffset = (IntPtr)((long)offset * ChunkMaskSizeInUlongs * sizeof(ulong)); GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _maskSsbo); GL.BufferSubData(BufferTarget.ShaderStorageBuffer, maskGpuOffset, ChunkMaskSizeInUlongs * sizeof(ulong), _maskUploadBuffer); } }); if (PerformanceMonitor.IsEnabled) { long end = Stopwatch.GetTimestamp(); PerformanceMonitor.Record(ThreadType.GpuRender, end - start); } }
    private int GetPageTableIndex(Vector3i chunkPos) => (chunkPos.X & MASK_X) + PT_X * ((chunkPos.Y & MASK_Y) + PT_Y * (chunkPos.Z & MASK_Z));
    private void UpdateDynamicObjectsAndGrid() { var voxelObjects = _worldManager.GetAllVoxelObjects(); int count = voxelObjects.Count; if (count > _tempGpuObjectsArray.Length) { Array.Resize(ref _tempGpuObjectsArray, count + 1024); GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer); GL.BufferData(BufferTarget.ShaderStorageBuffer, _tempGpuObjectsArray.Length * Marshal.SizeOf<GpuDynamicObject>(), IntPtr.Zero, BufferUsageHint.DynamicDraw); } if (count > 0) { var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }; Parallel.For(0, count, options, i => { var vo = voxelObjects[i]; Matrix4 model = Matrix4.CreateTranslation(-vo.LocalCenterOfMass) * Matrix4.CreateFromQuaternion(vo.Rotation) * Matrix4.CreateTranslation(vo.Position); var col = MaterialRegistry.GetColor(vo.Material); _tempGpuObjectsArray[i] = new GpuDynamicObject { Model = model, InvModel = Matrix4.Invert(model), Color = new Vector4(col.r, col.g, col.b, 1.0f), BoxMin = new Vector4(vo.LocalBoundsMin, 0), BoxMax = new Vector4(vo.LocalBoundsMax, 0) }; }); GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer); GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, count * Marshal.SizeOf<GpuDynamicObject>(), _tempGpuObjectsArray); } float snap = _gridCellSize; Vector3 playerPos = _worldManager.GetPlayerPosition(); Vector3 snappedCenter = new Vector3((float)Math.Floor(playerPos.X / snap) * snap, (float)Math.Floor(playerPos.Y / snap) * snap, (float)Math.Floor(playerPos.Z / snap) * snap); float halfExtent = (OBJ_GRID_SIZE * _gridCellSize) / 2.0f; _lastGridOrigin = snappedCenter - new Vector3(halfExtent); uint zero = 0; GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer); GL.BufferSubData(BufferTarget.AtomicCounterBuffer, IntPtr.Zero, sizeof(uint), ref zero); GL.ClearTexImage(_gridHeadTexture, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero); if (count > 0) { _gridComputeShader.Use(); _gridComputeShader.SetInt("uObjectCount", count); _gridComputeShader.SetVector3("uGridOrigin", _lastGridOrigin); _gridComputeShader.SetFloat("uGridStep", _gridCellSize); _gridComputeShader.SetInt("uGridSize", OBJ_GRID_SIZE); GL.BindImageTexture(1, _gridHeadTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32i); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo); GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, _atomicCounterBuffer); GL.DispatchCompute((count + 63) / 64, 1, 1); GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.AtomicCounterBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit); } }
    public void Render(Camera camera) { _shader.Use(); _shader.SetVector3("uCamPos", camera.Position); var view = camera.GetViewMatrix(); var proj = camera.GetProjectionMatrix(); _shader.SetMatrix4("uView", view); _shader.SetMatrix4("uProjection", proj); _shader.SetMatrix4("uInvView", Matrix4.Invert(view)); _shader.SetMatrix4("uInvProjection", Matrix4.Invert(proj)); _shader.SetFloat("uRenderDistance", _worldManager.GetViewRangeInMeters()); _shader.SetInt("uMaxRaySteps", (int)(GameSettings.RenderDistance * 3) + 32); _shader.SetVector3("uSunDir", Vector3.Normalize(new Vector3(0.2f, 0.4f, 0.8f))); _shader.SetFloat("uTime", _totalTime); _shader.SetInt("uSoftShadowSamples", GameSettings.SoftShadowSamples); Vector3 camPos = camera.Position; int chunkSz = Constants.ChunkSizeWorld; int camCx = (int)Math.Floor(camPos.X / chunkSz); int camCy = (int)Math.Floor(camPos.Y / chunkSz); int camCz = (int)Math.Floor(camPos.Z / chunkSz); int range = GameSettings.RenderDistance; int rangeBias = 1; _shader.SetInt("uBoundMinX", camCx - range - rangeBias); _shader.SetInt("uBoundMinY", 0); _shader.SetInt("uBoundMinZ", camCz - range - rangeBias); _shader.SetInt("uBoundMaxX", camCx + range + 1 + rangeBias); _shader.SetInt("uBoundMaxY", WorldManager.WorldHeightChunks); _shader.SetInt("uBoundMaxZ", camCz + range + 1 + rangeBias); GL.BindImageTexture(0, _pageTableTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32ui); GL.BindImageTexture(1, _gridHeadTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32i); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo); _shader.SetTexture("uNoiseTexture", _noiseTexture, TextureUnit.Texture0); _shader.SetVector3("uGridOrigin", _lastGridOrigin); _shader.SetFloat("uGridStep", _gridCellSize); _shader.SetInt("uGridSize", OBJ_GRID_SIZE); _shader.SetInt("uObjectCount", _worldManager.GetAllVoxelObjects().Count); GL.BindVertexArray(_quadVao); if (GameSettings.BeamOptimization) { GL.BindFramebuffer(FramebufferTarget.Framebuffer, _beamFbo); GL.Viewport(0, 0, _beamWidth, _beamHeight); GL.Clear(ClearBufferMask.ColorBufferBit); _shader.SetInt("uIsBeamPass", 1); GL.Disable(EnableCap.DepthTest); GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4); } GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); if (GameSettings.BeamOptimization) GL.Viewport(0, 0, _beamWidth * BeamDivisor, _beamHeight * BeamDivisor); else GL.Viewport(0, 0, _beamWidth * BeamDivisor, _beamHeight * BeamDivisor); _shader.SetInt("uIsBeamPass", 0); _shader.SetTexture("uBeamTexture", _beamTexture, TextureUnit.Texture1); GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4); GL.Enable(EnableCap.DepthTest); GL.BindVertexArray(0); }
    public void OnResize(int width, int height) { ResizeBeamBuffer(width, height); GL.Viewport(0, 0, width, height); }
    private void ResizeBeamBuffer(int screenWidth, int screenHeight) { _beamWidth = screenWidth / BeamDivisor; _beamHeight = screenHeight / BeamDivisor; if (_beamWidth < 1) _beamWidth = 1; if (_beamHeight < 1) _beamHeight = 1; GL.BindTexture(TextureTarget.Texture2D, _beamTexture); GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R32f, _beamWidth, _beamHeight, 0, PixelFormat.Red, PixelType.Float, IntPtr.Zero); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest); GL.BindTexture(TextureTarget.Texture2D, 0); GL.BindFramebuffer(FramebufferTarget.Framebuffer, _beamFbo); GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _beamTexture, 0); GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); }
    public void ReloadShader() { _shader?.Dispose(); var defines = new List<string>(); if (GameSettings.UseProceduralWater) defines.Add("WATER_MODE_PROCEDURAL"); if (GameSettings.EnableAO) defines.Add("ENABLE_AO"); if (GameSettings.EnableWaterTransparency) defines.Add("ENABLE_WATER_TRANSPARENCY"); if (GameSettings.BeamOptimization) defines.Add("ENABLE_BEAM_OPTIMIZATION"); switch (GameSettings.CurrentShadowMode) { case ShadowMode.Hard: defines.Add("SHADOW_MODE_HARD"); break; case ShadowMode.Soft: defines.Add("SHADOW_MODE_SOFT"); break; } try { _shader = new Shader("Shaders/raycast.vert", "Shaders/raycast.frag", defines); } catch (Exception ex) { Console.WriteLine($"[Renderer] Shader Error: {ex.Message}"); } }
    public void UploadAllVisibleChunks() { foreach (var c in _worldManager.GetChunksSnapshot()) NotifyChunkLoaded(c); }
    public void NotifyChunkModified(Chunk chunk) => NotifyChunkLoaded(chunk);
    private int LoadTexture(string path) { if (!File.Exists(path)) return 0; int handle = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, handle); StbImage.stbi_set_flip_vertically_on_load(1); using (Stream stream = File.OpenRead(path)) { ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha); GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, image.Width, image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, image.Data); } GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear); GL.GenerateMipmap(GenerateMipmapTarget.Texture2D); return handle; }
    public void Dispose() { CleanupBuffers(); _quadVao = 0; _shader?.Dispose(); _gridComputeShader?.Dispose(); }
}