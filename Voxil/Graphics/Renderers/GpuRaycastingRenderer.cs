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

    // Grid & LinkedList buffers
    private int _gridHeadTexture;
    private int _linkedListSsbo;
    private int _atomicCounterBuffer;
    private int _dynamicObjectsBuffer;

    // Beam params
    private int _beamFbo;
    private int _beamTexture;
    private int _beamWidth;
    private int _beamHeight;
    private const int BeamDivisor = 2;

    // Consts
    private const int ChunkMaskSizeInUlongs = Chunk.MasksCount;
    private readonly ulong[] _maskUploadBuffer = new ulong[ChunkMaskSizeInUlongs];
    private const int ChunkVol = Constants.ChunkVolume;
    private const int PackedChunkSizeInInts = ChunkVol / 4;
    private readonly uint[] _chunkUploadBuffer = new uint[PackedChunkSizeInInts];

    private const int PT_X = 512; private const int PT_Y = 16; private const int PT_Z = 512;
    private const int MASK_X = PT_X - 1; private const int MASK_Y = PT_Y - 1; private const int MASK_Z = PT_Z - 1;
    private const int OBJ_GRID_SIZE = 256;
    private float _gridCellSize;

    // === УПРАВЛЕНИЕ ПАМЯТЬЮ ===
    
    private Stack<int> _freeSlots = new Stack<int>();
    private Dictionary<Vector3i, int> _allocatedChunks = new Dictionary<Vector3i, int>();
    private ConcurrentQueue<Chunk> _uploadQueue = new ConcurrentQueue<Chunk>();
    private HashSet<Vector3i> _chunksPendingUpload = new HashSet<Vector3i>();

    // Page Table на CPU
    private readonly uint[] _cpuPageTable = new uint[PT_X * PT_Y * PT_Z];
    private bool _pageTableDirty = true;
    
    private int _currentCapacity;
    private float _totalTime = 0f;
    private int _noiseTexture;
    private Vector3 _lastGridOrigin;
    private GpuDynamicObject[] _tempGpuObjectsArray = new GpuDynamicObject[4096];

    [StructLayout(LayoutKind.Sequential)]
    struct GpuDynamicObject { public Matrix4 Model; public Matrix4 InvModel; public Vector4 Color; public Vector4 BoxMin; public Vector4 BoxMax; }

    public GpuRaycastingRenderer(WorldManager worldManager)
    {
        _worldManager = worldManager;
        _gridCellSize = Math.Max(Constants.VoxelSize, 0.5f);
        // Конструктор теперь легкий, вся инициализация OpenGL в методе Load()
    }

    public void Load()
    {
        // Шейдеры и текстуры грузим один раз
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

        // Инициализируем буферы первый раз
        InitializeBuffers();
        
        // Загружаем чанки, которые успели сгенерироваться до вызова Load
        UploadAllVisibleChunks();
    }

    // МЕТОД ИНИЦИАЛИЗАЦИИ БУФЕРОВ (Вызывается при старте и ресайзе)
    public void InitializeBuffers()
    {
        // 1. Очищаем старые ресурсы
        CleanupBuffers();

        // 2. Рассчитываем размер под ТЕКУЩУЮ настройку дальности
        int dist = Math.Max(GameSettings.RenderDistance, 4);
        // Небольшой запас для буфера
        int maxDist = dist + 2; 
        
        int estimatedChunks = (maxDist * 2 + 1) * (maxDist * 2 + 1) * WorldManager.WorldHeightChunks;
        
        long maxBufferBytes = 2048L * 1024 * 1024; // 2.0 GB Limit
        long bytesPerChunk = Constants.ChunkVolume; 
        int maxChunksByMem = (int)(maxBufferBytes / bytesPerChunk);

        _currentCapacity = Math.Min(estimatedChunks, maxChunksByMem);
        
        long maxAddressable = (long)uint.MaxValue / PackedChunkSizeInInts;
        if (_currentCapacity > maxAddressable) _currentCapacity = (int)maxAddressable - 1000;

        Console.WriteLine($"[Renderer] Reallocating buffers for {_currentCapacity} chunks (Dist: {dist})");

        // 3. Сбрасываем менеджер памяти
        _freeSlots.Clear();
        _allocatedChunks.Clear();
        for (int i = 0; i < _currentCapacity; i++) _freeSlots.Push(i);
        
        Array.Fill(_cpuPageTable, 0xFFFFFFFF);
        _chunksPendingUpload.Clear();
        while(!_uploadQueue.IsEmpty) _uploadQueue.TryDequeue(out _);

        // 4. Создаем буферы

        _pageTableTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32ui, PT_X, PT_Y, PT_Z, 0, PixelFormat.RedInteger, PixelType.UnsignedInt, _cpuPageTable);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);

        _voxelSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsbo);
        long totalBytes = (long)_currentCapacity * PackedChunkSizeInInts * sizeof(uint);
        try { GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)totalBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw); }
        catch (Exception ex) { Console.WriteLine($"[FATAL] GPU Mem: {ex.Message}"); throw; }
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);

        _maskSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _maskSsbo);
        long totalMaskBytes = (long)_currentCapacity * ChunkMaskSizeInUlongs * sizeof(ulong);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)totalMaskBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo);

        _gridHeadTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _gridHeadTexture);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32i, OBJ_GRID_SIZE, OBJ_GRID_SIZE, OBJ_GRID_SIZE, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);

        _linkedListSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _linkedListSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, 2 * 1024 * 1024 * 8, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo);

        _atomicCounterBuffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer);
        GL.BufferData(BufferTarget.AtomicCounterBuffer, sizeof(uint), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, _atomicCounterBuffer);

        _dynamicObjectsBuffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer);
        // Фикс размер буфера дин. объектов
        GL.BufferData(BufferTarget.ShaderStorageBuffer, 4096 * Marshal.SizeOf<GpuDynamicObject>(), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);

        _beamFbo = GL.GenFramebuffer();
        _beamTexture = GL.GenTexture();
        
        // Помечаем таблицу как грязную, чтобы залить нули
        _pageTableDirty = true;
    }

    private void CleanupBuffers()
    {
        if (_pageTableTexture != 0) GL.DeleteTexture(_pageTableTexture);
        if (_voxelSsbo != 0) GL.DeleteBuffer(_voxelSsbo);
        if (_maskSsbo != 0) GL.DeleteBuffer(_maskSsbo);
        if (_gridHeadTexture != 0) GL.DeleteTexture(_gridHeadTexture);
        if (_linkedListSsbo != 0) GL.DeleteBuffer(_linkedListSsbo);
        if (_atomicCounterBuffer != 0) GL.DeleteBuffer(_atomicCounterBuffer);
        if (_dynamicObjectsBuffer != 0) GL.DeleteBuffer(_dynamicObjectsBuffer);
        if (_beamFbo != 0) GL.DeleteFramebuffer(_beamFbo);
        if (_beamTexture != 0) GL.DeleteTexture(_beamTexture);

        _pageTableTexture = 0;
        _voxelSsbo = 0;
        _maskSsbo = 0;
        _gridHeadTexture = 0;
        _linkedListSsbo = 0;
        _atomicCounterBuffer = 0;
        _dynamicObjectsBuffer = 0;
        _beamFbo = 0;
        _beamTexture = 0;
    }
    
    // Внешний метод для вызова ресайза
    public void ResizeBuffers()
    {
        InitializeBuffers();
    }

    // === ГЛАВНЫЕ МЕТОДЫ УПРАВЛЕНИЯ ===

    public void UnloadChunk(Vector3i chunkPos)
    {
        if (_allocatedChunks.TryGetValue(chunkPos, out int slotIndex))
        {
            _freeSlots.Push(slotIndex);
            _allocatedChunks.Remove(chunkPos);

            _cpuPageTable[GetPageTableIndex(chunkPos)] = 0xFFFFFFFF;
            _pageTableDirty = true;
            
            lock(_chunksPendingUpload)
            {
                _chunksPendingUpload.Remove(chunkPos);
            }
        }
    }

    public void NotifyChunkLoaded(Chunk chunk)
    {
        if (chunk == null || !chunk.IsLoaded) return;
        if (chunk.SolidCount == 0) return;

        if (!_allocatedChunks.ContainsKey(chunk.Position))
        {
            if (_freeSlots.Count > 0)
            {
                int slot = _freeSlots.Pop();
                _allocatedChunks[chunk.Position] = slot;
                
                _cpuPageTable[GetPageTableIndex(chunk.Position)] = (uint)slot;
                _pageTableDirty = true;
            }
            else
            {
                return; // Нет памяти
            }
        }

        lock(_chunksPendingUpload)
        {
            if (_chunksPendingUpload.Add(chunk.Position))
            {
                _uploadQueue.Enqueue(chunk);
            }
        }
    }

    // === UPDATE LOOP ===

    public void Update(float deltaTime)
    {
        _totalTime += deltaTime;

        // 1. Выгрузка (Безлимитная, она дешевая)
        // В новой архитектуре UnloadChunk работает синхронно, но если вы используете очередь:
        // while (_chunksToUnload.TryDequeue...) { ... } - но у нас сейчас вызов идет из WorldManager напрямую.
        // Оставим только обработку очереди загрузки.

        // 2. ЗАГРУЗКА ДАННЫХ
        // БЮДЖЕТ: 8 мс.
        // Итого: 8мс (WorldManager) + 8мс (Renderer) + ~2мс (Render) = ~18мс на кадр.
        // Это ~55-60 FPS при полной загрузке. Как только очередь пустеет, FPS взлетает до небес.
        long maxTicks = Stopwatch.Frequency / 1000 * 8; 
        long startTicks = Stopwatch.GetTimestamp();

        while (_uploadQueue.TryDequeue(out var chunk))
        {
            bool isStillAllocated = false;
            int slot = -1;

            // Проверка валидности
            if (_allocatedChunks.TryGetValue(chunk.Position, out slot))
            {
                lock(_chunksPendingUpload)
                {
                    if (_chunksPendingUpload.Contains(chunk.Position))
                    {
                        _chunksPendingUpload.Remove(chunk.Position);
                        isStillAllocated = true;
                    }
                }
            }

            // Самая тяжелая часть
            if (isStillAllocated && chunk.IsLoaded)
            {
                UploadChunkVoxels(chunk, slot);
            }

            // Выходим строго по таймеру. Никаких лимитов по количеству (processedCount).
            // Если чанки пустые (воздух), мы их пролетим сотни за миллисекунду.
            // Если полные - успеем меньше, но зато не зафризим игру.
            if (Stopwatch.GetTimestamp() - startTicks > maxTicks) break;
        }

        if (_pageTableDirty)
        {
            GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, PT_X, PT_Y, PT_Z, PixelFormat.RedInteger, PixelType.UnsignedInt, _cpuPageTable);
            _pageTableDirty = false;
        }

        UpdateDynamicObjectsAndGrid();
    }

    // === ВНУТРЕННИЕ МЕТОДЫ ===

    private void UploadChunkVoxels(Chunk chunk, int offset)
    {
        chunk.ReadDataUnsafe((srcVoxels, srcMasks) =>
        {
            if (srcVoxels == null) return;

            System.Buffer.BlockCopy(srcVoxels, 0, _chunkUploadBuffer, 0, Constants.ChunkVolume);
            IntPtr gpuOffset = (IntPtr)((long)offset * PackedChunkSizeInInts * sizeof(uint));
            
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsbo);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, gpuOffset, PackedChunkSizeInInts * sizeof(uint), _chunkUploadBuffer);

            if (srcMasks != null)
            {
                System.Buffer.BlockCopy(srcMasks, 0, _maskUploadBuffer, 0, ChunkMaskSizeInUlongs * sizeof(ulong));
                IntPtr maskGpuOffset = (IntPtr)((long)offset * ChunkMaskSizeInUlongs * sizeof(ulong));
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _maskSsbo);
                GL.BufferSubData(BufferTarget.ShaderStorageBuffer, maskGpuOffset, ChunkMaskSizeInUlongs * sizeof(ulong), _maskUploadBuffer);
            }
        });
    }

    private int GetPageTableIndex(Vector3i chunkPos) => (chunkPos.X & MASK_X) + PT_X * ((chunkPos.Y & MASK_Y) + PT_Y * (chunkPos.Z & MASK_Z));

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
            Parallel.For(0, count, options, i =>
            {
                var vo = voxelObjects[i];
                Matrix4 model = Matrix4.CreateTranslation(-vo.LocalCenterOfMass) * Matrix4.CreateFromQuaternion(vo.Rotation) * Matrix4.CreateTranslation(vo.Position);
                var col = MaterialRegistry.GetColor(vo.Material);

                _tempGpuObjectsArray[i] = new GpuDynamicObject
                {
                    Model = model,
                    InvModel = Matrix4.Invert(model),
                    Color = new Vector4(col.r, col.g, col.b, 1.0f),
                    BoxMin = new Vector4(vo.LocalBoundsMin, 0),
                    BoxMax = new Vector4(vo.LocalBoundsMax, 0)
                };
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

    public void Render(Camera camera)
    {
        _shader.Use();
        _shader.SetVector3("uCamPos", camera.Position);

        var view = camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix();
        _shader.SetMatrix4("uView", view);
        _shader.SetMatrix4("uProjection", proj);
        _shader.SetMatrix4("uInvView", Matrix4.Invert(view));
        _shader.SetMatrix4("uInvProjection", Matrix4.Invert(proj));

        _shader.SetFloat("uRenderDistance", _worldManager.GetViewRangeInMeters());
        _shader.SetInt("uMaxRaySteps", (int)(GameSettings.RenderDistance * 3) + 32);
        _shader.SetVector3("uSunDir", Vector3.Normalize(new Vector3(0.2f, 0.4f, 0.8f)));
        _shader.SetFloat("uTime", _totalTime);
        _shader.SetInt("uSoftShadowSamples", GameSettings.SoftShadowSamples);

        Vector3 camPos = camera.Position;
        int camCx = (int)Math.Floor(camPos.X / Constants.ChunkSizeWorld);
        int camCy = (int)Math.Floor(camPos.Y / Constants.ChunkSizeWorld);
        int camCz = (int)Math.Floor(camPos.Z / Constants.ChunkSizeWorld);
        int range = GameSettings.RenderDistance;
        int rangeBias = 1;

        _shader.SetInt("uBoundMinX", camCx - range - rangeBias);
        _shader.SetInt("uBoundMinY", 0);
        _shader.SetInt("uBoundMinZ", camCz - range - rangeBias);
        _shader.SetInt("uBoundMaxX", camCx + range + 1 + rangeBias);
        _shader.SetInt("uBoundMaxY", WorldManager.WorldHeightChunks);
        _shader.SetInt("uBoundMaxZ", camCz + range + 1 + rangeBias);

        GL.BindImageTexture(0, _pageTableTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32ui);
        GL.BindImageTexture(1, _gridHeadTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32i);

        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo);

        _shader.SetTexture("uNoiseTexture", _noiseTexture, TextureUnit.Texture0);
        _shader.SetVector3("uGridOrigin", _lastGridOrigin);
        _shader.SetFloat("uGridStep", _gridCellSize);
        _shader.SetInt("uGridSize", OBJ_GRID_SIZE);
        _shader.SetInt("uObjectCount", _worldManager.GetAllVoxelObjects().Count);

        GL.BindVertexArray(_quadVao);

        if (GameSettings.BeamOptimization)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _beamFbo);
            GL.Viewport(0, 0, _beamWidth, _beamHeight);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            
            _shader.SetInt("uIsBeamPass", 1); 
            GL.Disable(EnableCap.DepthTest);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }
        
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        
        if (GameSettings.BeamOptimization)
            GL.Viewport(0, 0, _beamWidth * BeamDivisor, _beamHeight * BeamDivisor); 
        else
            GL.Viewport(0, 0, _beamWidth * BeamDivisor, _beamHeight * BeamDivisor); 
        
        _shader.SetInt("uIsBeamPass", 0);
        _shader.SetTexture("uBeamTexture", _beamTexture, TextureUnit.Texture1); 
        
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        
        GL.Enable(EnableCap.DepthTest);
        GL.BindVertexArray(0);
    }

    public void OnResize(int width, int height)
    {
        ResizeBeamBuffer(width, height);
        GL.Viewport(0, 0, width, height);
    }

    private void ResizeBeamBuffer(int screenWidth, int screenHeight)
    {
        _beamWidth = screenWidth / BeamDivisor;
        _beamHeight = screenHeight / BeamDivisor;
        if (_beamWidth < 1) _beamWidth = 1;
        if (_beamHeight < 1) _beamHeight = 1;

        GL.BindTexture(TextureTarget.Texture2D, _beamTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R32f, _beamWidth, _beamHeight, 0, PixelFormat.Red, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _beamFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _beamTexture, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

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

        try { _shader = new Shader("Shaders/raycast.vert", "Shaders/raycast.frag", defines); }
        catch (Exception ex) { Console.WriteLine($"[Renderer] Shader Error: {ex.Message}"); }
    }
    
    public void UploadAllVisibleChunks() { foreach (var c in _worldManager.GetChunksSnapshot()) NotifyChunkLoaded(c); }
    public void NotifyChunkModified(Chunk chunk) => NotifyChunkLoaded(chunk);

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

    public void Dispose()
    {
        CleanupBuffers();
        _quadVao = 0;
        _shader?.Dispose();
        _gridComputeShader?.Dispose();
    }
}