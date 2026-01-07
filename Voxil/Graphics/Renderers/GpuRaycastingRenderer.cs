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
    
    // === НОВЫЕ БУФЕРЫ ДЛЯ LINKED LIST ===
    private int _gridHeadTexture;       // Текстура-голова (R32I)
    private int _linkedListSsbo;        // Буфер узлов (nodes)
    private int _atomicCounterBuffer;   // Счетчик атомик (для аллокации узлов)
    
    private int _dynamicObjectsBuffer;  

    private const int ChunkVol = Constants.ChunkVolume; 
    private const int PackedChunkSizeInInts = ChunkVol / 4; 

    private const int PT_X = 512; private const int PT_Y = 16; private const int PT_Z = 512;
    private const int MASK_X = PT_X - 1; private const int MASK_Y = PT_Y - 1; private const int MASK_Z = PT_Z - 1;

    // Сетка 256 (оптимально для 16GB)
    private const int OBJ_GRID_SIZE = 256; 
    private float _gridCellSize; 

    // Максимальное кол-во узлов в списке (на весь мир). 
    // 2 миллиона узлов = ~16 МБ памяти. С головой хватит для 3000 объектов.
    private const int MAX_LINKED_LIST_NODES = 2 * 1024 * 1024;

    private int _currentCapacity;
    private readonly int[] _cpuPageTable = new int[PT_X * PT_Y * PT_Z];
    private bool _pageTableDirty = true;
    
    private Stack<int> _freeSlots = new Stack<int>();
    private Dictionary<Vector3i, int> _loadedChunks = new Dictionary<Vector3i, int>();
    private ConcurrentQueue<Chunk> _chunksToUpload = new ConcurrentQueue<Chunk>();
    private ConcurrentQueue<Vector3i> _chunksToUnload = new ConcurrentQueue<Vector3i>();
    private HashSet<Vector3i> _queuedChunkPositions = new HashSet<Vector3i>();

    private readonly uint[] _chunkUploadBuffer = new uint[PackedChunkSizeInInts];
    private GpuDynamicObject[] _tempGpuObjectsArray = new GpuDynamicObject[4096]; 

    private Stopwatch _stopwatch = new Stopwatch();
    private float _totalTime = 0f;
    private int _noiseTexture;

    [StructLayout(LayoutKind.Sequential)]
    struct GpuDynamicObject { public Matrix4 Model; public Matrix4 InvModel; public Vector4 Color; public Vector4 BoxMin; public Vector4 BoxMax; }

    public GpuRaycastingRenderer(WorldManager worldManager)
    {
        _worldManager = worldManager;
        
        // Размер ячейки. 0.5 метра - хороший баланс.
        _gridCellSize = Math.Max(Constants.VoxelSize, 0.5f);

        int dist = Math.Max(GameSettings.RenderDistance, 32);
        int estimatedChunks = (dist * 2 + 1) * (dist * 2 + 1) * WorldManager.WorldHeightChunks;
        
        _currentCapacity = estimatedChunks; 
        long maxAddressable = int.MaxValue / PackedChunkSizeInInts;
        if (_currentCapacity > maxAddressable) _currentCapacity = (int)maxAddressable - 1000;

        Console.WriteLine($"[Renderer] Linked List Grid: {OBJ_GRID_SIZE}^3, Cell: {_gridCellSize:F3}m");
        
        for (int i = 0; i < _currentCapacity; i++) _freeSlots.Push(i);
        Array.Fill(_cpuPageTable, -1);
    }

    public void Load()
    {
        ReloadShader();
        _gridComputeShader = new Shader("Shaders/grid_update.comp");

        _quadVao = GL.GenVertexArray(); GL.BindVertexArray(_quadVao);
        int dummyVbo = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ArrayBuffer, dummyVbo);
        float[] dummyData = new float[8] { -1, -1, 1, -1, -1, 1, 1, 1 };
        GL.BufferData(BufferTarget.ArrayBuffer, sizeof(float) * 8, dummyData, BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0); GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        _noiseTexture = LoadTexture("Shaders/Images/water_noise.png");
        
        // Page Table
        _pageTableTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32i, PT_X, PT_Y, PT_Z, 0, PixelFormat.RedInteger, PixelType.Int, _cpuPageTable);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge); 
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);

        // Voxel SSBO
        _voxelSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsbo);
        long totalBytes = (long)_currentCapacity * PackedChunkSizeInInts * sizeof(uint);
        try { GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)totalBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw); } 
        catch (Exception ex) { Console.WriteLine($"[FATAL] GPU Mem: {ex.Message}"); throw; }
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);

        // === 1. GRID HEAD TEXTURE (R32I) ===
        // Хранит индекс первого узла в списке. -1 если пусто.
        _gridHeadTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _gridHeadTexture);
        // Заполняем нулями? Нет, нужно инициализировать -1. Но ClearTexImage с Int сделает 0. 
        // Мы будем использовать 0 как "пусто" в логике шейдера (индексы сдвинем +1), или чистить compute shader'ом.
        // Проще всего чистить Compute Shader'ом или PBO, но раз PBO крашил, будем использовать 0 как null.
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32i, OBJ_GRID_SIZE, OBJ_GRID_SIZE, OBJ_GRID_SIZE, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        
        // === 2. LINKED LIST NODES (SSBO) ===
        // Struct Node { uint objectID; int nextNode; } = 8 байт
        _linkedListSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _linkedListSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, MAX_LINKED_LIST_NODES * 8, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo); // Binding 3

        // === 3. ATOMIC COUNTER ===
        _atomicCounterBuffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer);
        GL.BufferData(BufferTarget.AtomicCounterBuffer, sizeof(uint), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, _atomicCounterBuffer); // Binding 4

        // Dynamic Objects Data
        _dynamicObjectsBuffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer);
        int maxObjectsBytes = _tempGpuObjectsArray.Length * Marshal.SizeOf<GpuDynamicObject>();
        GL.BufferData(BufferTarget.ShaderStorageBuffer, maxObjectsBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);

        UploadAllVisibleChunks();
    }

    public void Update(float deltaTime)
    {
        if (PerformanceMonitor.IsEnabled) _stopwatch.Restart();
        _totalTime += deltaTime;

        int unloads = 0;
        while (unloads < 200 && _chunksToUnload.TryDequeue(out var pos)) { ProcessUnload(pos); unloads++; }

        int uploads = 0;
        int limit = GameSettings.GpuUploadSpeed; 
        while (uploads < limit && _chunksToUpload.TryDequeue(out var chunk)) { _queuedChunkPositions.Remove(chunk.Position); UploadChunkVoxels(chunk); uploads++; }

        if (_pageTableDirty) {
            GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, PT_X, PT_Y, PT_Z, PixelFormat.RedInteger, PixelType.Int, _cpuPageTable);
            _pageTableDirty = false;
        }

        UpdateDynamicObjectsAndGrid();
        if (PerformanceMonitor.IsEnabled) { _stopwatch.Stop(); PerformanceMonitor.Record(ThreadType.GpuRender, _stopwatch.ElapsedTicks); }
    }

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

        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        Parallel.For(0, count, options, i =>
        {
            var vo = voxelObjects[i];
            Matrix4 model = Matrix4.CreateTranslation(-vo.LocalCenterOfMass) * Matrix4.CreateFromQuaternion(vo.Rotation) * Matrix4.CreateTranslation(vo.Position);
            Matrix4 invModel = Matrix4.Invert(model); 
            var col = MaterialRegistry.GetColor(vo.Material);
            _tempGpuObjectsArray[i] = new GpuDynamicObject
            {
                Model = model, InvModel = invModel, Color = new Vector4(col.r, col.g, col.b, 1.0f),
                BoxMin = new Vector4(vo.LocalBoundsMin + vo.LocalCenterOfMass, 0),
                BoxMax = new Vector4(vo.LocalBoundsMax + vo.LocalCenterOfMass, 0)
            };
        });

        if (count > 0)
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, count * Marshal.SizeOf<GpuDynamicObject>(), _tempGpuObjectsArray);
        }

        float snap = _gridCellSize;
        Vector3 snappedCenter = new Vector3(
            (float)Math.Floor(_worldManager.GetPlayerPosition().X / snap) * snap,
            (float)Math.Floor(_worldManager.GetPlayerPosition().Y / snap) * snap,
            (float)Math.Floor(_worldManager.GetPlayerPosition().Z / snap) * snap
        );
        float halfExtent = (OBJ_GRID_SIZE * _gridCellSize) / 2.0f;
        Vector3 gridOrigin = snappedCenter - new Vector3(halfExtent);
        _lastGridOrigin = gridOrigin;

        // 1. Очищаем счетчик атомиков
        uint zero = 0;
        GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer);
        GL.BufferSubData(BufferTarget.AtomicCounterBuffer, IntPtr.Zero, sizeof(uint), ref zero);
        GL.BindBuffer(BufferTarget.AtomicCounterBuffer, 0);

        // 2. Очищаем Head Texture (-1 или 0). Используем 0, так как это дефолт для Clear.
        // В шейдере: 0 = null, 1+ = индекс.
        GL.ClearTexImage(_gridHeadTexture, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero);

        if (count > 0)
        {
            _gridComputeShader.Use();
            _gridComputeShader.SetInt("uObjectCount", count);
            _gridComputeShader.SetVector3("uGridOrigin", gridOrigin);
            _gridComputeShader.SetFloat("uGridStep", _gridCellSize);
            _gridComputeShader.SetInt("uGridSize", OBJ_GRID_SIZE);
            
            // Image Binding 0 used for PageTable, 1 for GridHead
            GL.BindImageTexture(1, _gridHeadTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32i); 
            
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo); // Nodes
            GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, _atomicCounterBuffer); // Counter

            GL.DispatchCompute((count + 63) / 64, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.AtomicCounterBarrierBit);
        }
    }

    private Vector3 _lastGridOrigin;

    public void Render(Camera camera)
    {
        if (PerformanceMonitor.IsEnabled) _stopwatch.Restart();

        _shader.Use();
        _shader.SetVector3("uCamPos", camera.Position);
        var view = camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix();
        var invView = Matrix4.Invert(view);
        var invProj = Matrix4.Invert(proj);
        _shader.SetMatrix4("uView", view);
        _shader.SetMatrix4("uProjection", proj);
        _shader.SetMatrix4("uInvView", invView);       // <--- НОВОЕ
        _shader.SetMatrix4("uInvProjection", invProj); // <--- НОВОЕ
        _shader.SetFloat("uRenderDistance", _worldManager.GetViewRangeInMeters());
        
        int maxRaySteps = (int)(GameSettings.RenderDistance * 3) + 32; 
        _shader.SetInt("uMaxRaySteps", maxRaySteps);

        _shader.SetVector3("uSunDir", Vector3.Normalize(new Vector3(0.0f, 0.3f, 0.8f)));
        _shader.SetFloat("uTime", _totalTime);
        _shader.SetInt("uSoftShadowSamples", GameSettings.SoftShadowSamples);

        Vector3 camPos = camera.Position;
        int camCx = (int)Math.Floor(camPos.X / Constants.ChunkSizeWorld);
        int camCy = (int)Math.Floor(camPos.Y / Constants.ChunkSizeWorld);
        int camCz = (int)Math.Floor(camPos.Z / Constants.ChunkSizeWorld);
        int range = GameSettings.RenderDistance;

        _shader.SetInt("uBoundMinX", camCx - range);
        _shader.SetInt("uBoundMinY", 0);
        _shader.SetInt("uBoundMinZ", camCz - range);
        _shader.SetInt("uBoundMaxX", camCx + range + 1);
        _shader.SetInt("uBoundMaxY", WorldManager.WorldHeightChunks);
        _shader.SetInt("uBoundMaxZ", camCz + range + 1);

        GL.BindImageTexture(0, _pageTableTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32i);
        // Binding 1: Grid Head
        GL.BindImageTexture(1, _gridHeadTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32i);
        
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo); // Nodes read

        _shader.SetTexture("uNoiseTexture", _noiseTexture, TextureUnit.Texture0);
        _shader.SetVector3("uGridOrigin", _lastGridOrigin);
        _shader.SetFloat("uGridStep", _gridCellSize);
        _shader.SetInt("uGridSize", OBJ_GRID_SIZE);

        int dynObjectCount = _worldManager.GetAllVoxelObjects().Count;
        _shader.SetInt("uObjectCount", dynObjectCount);

        GL.Disable(EnableCap.DepthTest);
        GL.BindVertexArray(_quadVao);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        GL.Enable(EnableCap.DepthTest);

        if (PerformanceMonitor.IsEnabled) { _stopwatch.Stop(); PerformanceMonitor.Record(ThreadType.GpuRender, _stopwatch.ElapsedTicks); }
    }

    public void ReloadShader()
    {
        _shader?.Dispose();
        var defines = new List<string>();
        if (GameSettings.UseProceduralWater) defines.Add("WATER_MODE_PROCEDURAL");
        switch (GameSettings.CurrentShadowMode)
        {
            case ShadowMode.Hard: defines.Add("SHADOW_MODE_HARD"); break;
            case ShadowMode.Soft: defines.Add("SHADOW_MODE_SOFT"); break;
        }
        try { _shader = new Shader("Shaders/raycast.vert", "Shaders/raycast.frag", defines); }
        catch (Exception ex) { Console.WriteLine($"[Renderer] Shader Error: {ex.Message}"); }
    }

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

    private int LoadTexture(string path)
    {
        if (!File.Exists(path)) return 0;
        int handle = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, handle);
        StbImage.stbi_set_flip_vertically_on_load(1);
        using (Stream stream = File.OpenRead(path)) {
            ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, image.Width, image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, image.Data);
        }
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        return handle;
    }

    public void UploadChunkVoxels(Chunk chunk)
    {
        if (chunk == null || !chunk.IsLoaded) return;
        var src = chunk.GetVoxelsUnsafe();

        if (src == null)
        {
            if (_loadedChunks.TryGetValue(chunk.Position, out int oldOffset))
            {
                _freeSlots.Push(oldOffset);
                _loadedChunks.Remove(chunk.Position);
            }
            _cpuPageTable[GetPageTableIndex(chunk.Position)] = -1;
            _pageTableDirty = true;
            return;
        }
        
        int offset;
        if (_loadedChunks.TryGetValue(chunk.Position, out int existingOffset)) 
        {
            offset = existingOffset;
        }
        else
        {
            if (_freeSlots.Count == 0) ResizeBuffer();
            if (_freeSlots.Count == 0) 
            {
                Console.WriteLine("[Renderer] VRAM FULL!");
                return; 
            }

            offset = _freeSlots.Pop();
            _loadedChunks[chunk.Position] = offset;
        }

        System.Buffer.BlockCopy(src, 0, _chunkUploadBuffer, 0, Constants.ChunkVolume);

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
        long maxAddressable = int.MaxValue / PackedChunkSizeInInts;
        if (newCapacity > maxAddressable) newCapacity = (int)maxAddressable;
        if (newCapacity <= _currentCapacity) return;

        long oldBytes = (long)_currentCapacity * PackedChunkSizeInInts * sizeof(uint);
        long newBytes = (long)newCapacity * PackedChunkSizeInInts * sizeof(uint);
        
        Console.WriteLine($"[Renderer] Resizing Voxel SSBO: {_currentCapacity} -> {newCapacity}");

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

    public void UploadAllVisibleChunks() { var chunks = _worldManager.GetChunksSnapshot(); foreach (var c in chunks) NotifyChunkLoaded(c); }
    public void NotifyChunkLoaded(Chunk chunk) { if (chunk != null && chunk.IsLoaded) { if (_queuedChunkPositions.Add(chunk.Position)) _chunksToUpload.Enqueue(chunk); } }
    public void UnloadChunk(Vector3i chunkPos) => _chunksToUnload.Enqueue(chunkPos);
    public void OnResize(int width, int height) => GL.Viewport(0, 0, width, height);
    public void Dispose() {
        GL.DeleteTexture(_pageTableTexture); GL.DeleteTexture(_gridHeadTexture); GL.DeleteTexture(_noiseTexture);
        GL.DeleteBuffer(_voxelSsbo); GL.DeleteBuffer(_dynamicObjectsBuffer); GL.DeleteVertexArray(_quadVao);
        _shader?.Dispose(); _gridComputeShader?.Dispose();
    }
}