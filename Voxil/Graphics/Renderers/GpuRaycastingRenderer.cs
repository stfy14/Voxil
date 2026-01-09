using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.IO;
using StbImageSharp;
using System.Threading.Tasks;

public class GpuRaycastingRenderer : IDisposable
{
    // ... (стандартные поля Shader, VAO, WorldManager) ...
    private Shader _shader;
    private Shader _gridComputeShader;
    private int _quadVao;
    private readonly WorldManager _worldManager;

    private int _pageTableTexture;
    
    // ИЗМЕНЕНИЕ: ВМЕСТО ОДНОГО БУФЕРА ДЕЛАЕМ 4 БАНКА
    private const int NUM_VOXEL_BANKS = 4;
    private int[] _voxelSsboBanks = new int[NUM_VOXEL_BANKS]; 
    
    private int _maskSsbo;

    // ... (остальные буферы: GridHead, LinkedList, Atomic, Dynamic, Beam) ...
    private int _gridHeadTexture;
    private int _linkedListSsbo;
    private int _atomicCounterBuffer;
    private int _dynamicObjectsBuffer;
    private int _beamFbo;
    private int _beamTexture;
    private int _beamWidth;
    private int _beamHeight;
    private const int BeamDivisor = 2;

    // Consts
    private const int ChunkVol = Constants.ChunkVolume;
    private const int PackedChunkSizeInInts = ChunkVol / 4;
    private const int ChunkMaskSizeInUlongs = Chunk.MasksCount;

    private readonly ulong[] _maskUploadBuffer = new ulong[ChunkMaskSizeInUlongs];
    private readonly uint[] _chunkUploadBuffer = new uint[PackedChunkSizeInInts];

    private const int PT_X = 512, PT_Y = 16, PT_Z = 512;
    private const int MASK_X = PT_X - 1, MASK_Y = PT_Y - 1, MASK_Z = PT_Z - 1;
    private const int OBJ_GRID_SIZE = 256;
    private float _gridCellSize;

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
        _gridCellSize = Math.Max(Constants.VoxelSize, 0.5f);
        TotalVramMb = DetectTotalVram();
    }

    public void Load()
    {
        ReloadShader();
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
    
    public void RequestReallocation()
    {
        CleanupBuffers(); 
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        _reallocationPending = true; _currentCapacity = 0;
    }

    public bool IsReallocationPending() => _reallocationPending;
    
    public void PerformReallocation()
    {
        if (!_reallocationPending) return;
        InitializeBuffers();
        ReloadShader();
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

        if (_voxelSsboBanks[0] != 0 && _currentCapacity == requestedChunks) return;

        // Расчет памяти
        long totalVBytes = (long)requestedChunks * PackedChunkSizeInInts * 4;
        long mBytes = (long)requestedChunks * ChunkMaskSizeInUlongs * 8;
        
        // Делим воксели на 4 банка
        long bytesPerBank = totalVBytes / NUM_VOXEL_BANKS;
        // Округляем вверх до размера чанка, чтобы не разрывать данные
        long chunkSizeInBytes = PackedChunkSizeInInts * 4;
        long chunksPerBank = requestedChunks / NUM_VOXEL_BANKS + 1;
        bytesPerBank = chunksPerBank * chunkSizeInBytes;

        if (_voxelSsboBanks[0] == 0)
        {
            // Создаем 4 буфера
            for(int i=0; i<NUM_VOXEL_BANKS; i++) _voxelSsboBanks[i] = GL.GenBuffer();
            _maskSsbo = GL.GenBuffer(); 
            _pageTableTexture = GL.GenTexture();
            CreateAuxBuffers();
        }

        _currentCapacity = requestedChunks;
        CurrentAllocatedBytes = (bytesPerBank * NUM_VOXEL_BANKS) + mBytes;

        Console.WriteLine($"[Renderer] Allocating 4 Voxel Banks. Each: {bytesPerBank / 1024 / 1024} MB");

        // Выделяем память для каждого банка
        for(int i=0; i<NUM_VOXEL_BANKS; i++)
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsboBanks[i]);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)bytesPerBank, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            // Привязываем к слотам 1, 6, 7, 8
            int bindingPoint = (i == 0) ? 1 : (5 + i); // 1, 6, 7, 8
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, bindingPoint, _voxelSsboBanks[i]);
        }

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _maskSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)mBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo);

        GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32ui, PT_X, PT_Y, PT_Z, 0, PixelFormat.RedInteger, PixelType.UnsignedInt, _cpuPageTable);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        
        ResetAllocationLogic(_currentCapacity);
    }

    private void ResetAllocationLogic(int capacity)
    {
        _freeSlots.Clear(); _allocatedChunks.Clear();
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
        for(int i=0; i<9; i++) GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, i, 0);
        GL.Flush(); GL.Finish();
        
        for(int i=0; i<NUM_VOXEL_BANKS; i++) {
            if (_voxelSsboBanks[i] != 0) { GL.DeleteBuffer(_voxelSsboBanks[i]); _voxelSsboBanks[i] = 0; }
        }
        
        if (_maskSsbo != 0) { GL.DeleteBuffer(_maskSsbo); _maskSsbo = 0; }
        if (_pageTableTexture != 0) { GL.DeleteTexture(_pageTableTexture); _pageTableTexture = 0; }
        
        CurrentAllocatedBytes = 0; _currentCapacity = 0;
        System.Threading.Thread.Sleep(50);
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

    private void UploadChunkVoxels(Chunk chunk, int globalSlot) 
    { 
        chunk.ReadDataUnsafe((srcVoxels, srcMasks) => { 
            if (srcVoxels == null) return; 
            
            // Определяем банк и локальный слот
            int bank = globalSlot % NUM_VOXEL_BANKS;
            int localSlot = globalSlot / NUM_VOXEL_BANKS;
            
            // Загрузка вокселей в нужный банк
            System.Buffer.BlockCopy(srcVoxels, 0, _chunkUploadBuffer, 0, ChunkVol); 
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsboBanks[bank]); 
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)((long)localSlot * PackedChunkSizeInInts * 4), PackedChunkSizeInInts * 4, _chunkUploadBuffer); 
            
            // Загрузка масок (Маски остаются в одном буфере, так как они маленькие)
            if (srcMasks != null) { 
                System.Buffer.BlockCopy(srcMasks, 0, _maskUploadBuffer, 0, ChunkMaskSizeInUlongs * 8); 
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _maskSsbo); 
                GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)((long)globalSlot * ChunkMaskSizeInUlongs * 8), ChunkMaskSizeInUlongs * 8, _maskUploadBuffer); 
            } 
        }); 
    }

    private int GetPageTableIndex(Vector3i p) => (p.X & MASK_X) + PT_X * ((p.Y & MASK_Y) + PT_Y * (p.Z & MASK_Z));
    
    private void UpdateDynamicObjectsAndGrid() 
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
            var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }; 
            Parallel.For(0, count, options, i => { 
                var vo = voxelObjects[i]; 
                Matrix4 model = Matrix4.CreateTranslation(-vo.LocalCenterOfMass) * Matrix4.CreateFromQuaternion(vo.Rotation) * Matrix4.CreateTranslation(vo.Position); 
                var col = MaterialRegistry.GetColor(vo.Material); 
                _tempGpuObjectsArray[i] = new GpuDynamicObject { Model = model, InvModel = Matrix4.Invert(model), Color = new Vector4(col.r, col.g, col.b, 1.0f), BoxMin = new Vector4(vo.LocalBoundsMin, 0), BoxMax = new Vector4(vo.LocalBoundsMax, 0) }; 
            }); 
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer); 
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, count * Marshal.SizeOf<GpuDynamicObject>(), _tempGpuObjectsArray); 
        } 
        float snap = _gridCellSize; 
        Vector3 playerPos = _worldManager.GetPlayerPosition(); 
        Vector3 snappedCenter = new Vector3((float)Math.Floor(playerPos.X / snap) * snap, (float)Math.Floor(playerPos.Y / snap) * snap, (float)Math.Floor(playerPos.Z / snap) * snap); 
        float halfExtent = (OBJ_GRID_SIZE * _gridCellSize) / 2.0f; 
        _lastGridOrigin = snappedCenter - new Vector3(halfExtent); 
        uint zero = 0; 
        GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer); 
        GL.BufferSubData(BufferTarget.AtomicCounterBuffer, IntPtr.Zero, sizeof(uint), ref zero); 
        GL.ClearTexImage(_gridHeadTexture, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero); 
        if (count > 0) 
        { 
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
        _shader.Use(); 
        _shader.SetVector3("uCamPos", cam.Position); 
        _shader.SetMatrix4("uView", cam.GetViewMatrix()); _shader.SetMatrix4("uProjection", cam.GetProjectionMatrix()); 
        _shader.SetMatrix4("uInvView", Matrix4.Invert(cam.GetViewMatrix())); _shader.SetMatrix4("uInvProjection", Matrix4.Invert(cam.GetProjectionMatrix())); 
        _shader.SetFloat("uRenderDistance", _worldManager.GetViewRangeInMeters()); 
        _shader.SetInt("uMaxRaySteps", (int)(GameSettings.RenderDistance * 8) + 128); 
        _shader.SetVector3("uSunDir", Vector3.Normalize(new Vector3(0.2f, 0.4f, 0.8f))); 
        _shader.SetFloat("uTime", _totalTime); 
        _shader.SetInt("uShowDebugHeatmap", GameSettings.ShowDebugHeatmap ? 1 : 0);

        Vector3 p = cam.Position; int sz = Constants.ChunkSizeWorld; 
        int cx = (int)Math.Floor(p.X/sz), cy = (int)Math.Floor(p.Y/sz), cz = (int)Math.Floor(p.Z/sz); 
        int r = GameSettings.RenderDistance + 2; 
        _shader.SetInt("uBoundMinX", cx-r); _shader.SetInt("uBoundMinY", 0); _shader.SetInt("uBoundMinZ", cz-r); 
        _shader.SetInt("uBoundMaxX", cx+r); _shader.SetInt("uBoundMaxY", WorldManager.WorldHeightChunks); _shader.SetInt("uBoundMaxZ", cz+r); 
        
        GL.BindImageTexture(0, _pageTableTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32ui); 
        
        // ПРИВЯЗЫВАЕМ ВСЕ БАНКИ ДЛЯ РЕНДЕРА
        for(int i=0; i<NUM_VOXEL_BANKS; i++) {
            int binding = (i==0) ? 1 : (5+i);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, binding, _voxelSsboBanks[i]);
        }
        
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo); 
        
        GL.BindVertexArray(_quadVao); 
        GL.Viewport(0, 0, _beamWidth * BeamDivisor, _beamHeight * BeamDivisor); 
        _shader.SetInt("uIsBeamPass", 0); 
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4); 
    }
    
    public void OnResize(int w, int h) { _beamWidth = w/2; _beamHeight = h/2; GL.Viewport(0,0,w,h); }
    
    public void ReloadShader() 
    { 
        _shader?.Dispose();
        var defines = new List<string>();
        if (GameSettings.UseProceduralWater) defines.Add("WATER_MODE_PROCEDURAL");
        if (GameSettings.EnableAO) defines.Add("ENABLE_AO");
        if (GameSettings.EnableWaterTransparency) defines.Add("ENABLE_WATER_TRANSPARENCY");
        if (GameSettings.BeamOptimization) defines.Add("ENABLE_BEAM_OPTIMIZATION");
        switch (GameSettings.CurrentShadowMode)
        {
            case ShadowMode.Hard: defines.Add("SHADOW_MODE_HARD"); break;
            case ShadowMode.Soft: defines.Add("SHADOW_MODE_SOFT"); break;
        }
        try { _shader = new Shader("Shaders/raycast.vert", "Shaders/raycast.frag", defines); } catch (Exception ex) { Console.WriteLine($"[Renderer] SHADER ERROR: {ex.Message}"); } 
    }
    
    public void UploadAllVisibleChunks() { foreach (var c in _worldManager.GetChunksSnapshot()) NotifyChunkLoaded(c); }
    public void Dispose() { CleanupBuffers(); }
    private int LoadTexture(string path) { if (!File.Exists(path)) return 0; int handle = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, handle); StbImage.stbi_set_flip_vertically_on_load(1); using (Stream stream = File.OpenRead(path)) { ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha); GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, image.Width, image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, image.Data); } GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear); GL.GenerateMipmap(GenerateMipmapTarget.Texture2D); return handle; }
}