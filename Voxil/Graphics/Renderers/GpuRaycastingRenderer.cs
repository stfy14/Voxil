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

    private int _gridHeadTexture;
    private int _linkedListSsbo;
    private int _atomicCounterBuffer;

    private int _dynamicObjectsBuffer;

    private const int ChunkVol = Constants.ChunkVolume;
    private const int PackedChunkSizeInInts = ChunkVol / 4;

    private const int PT_X = 512; private const int PT_Y = 16; private const int PT_Z = 512;
    private const int MASK_X = PT_X - 1; private const int MASK_Y = PT_Y - 1; private const int MASK_Z = PT_Z - 1;

    private const int OBJ_GRID_SIZE = 256;
    private float _gridCellSize;

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

    private float _totalTime = 0f;
    private int _noiseTexture;
    private Vector3 _lastGridOrigin;

    [StructLayout(LayoutKind.Sequential)]
    struct GpuDynamicObject { public Matrix4 Model; public Matrix4 InvModel; public Vector4 Color; public Vector4 BoxMin; public Vector4 BoxMax; }

    public GpuRaycastingRenderer(WorldManager worldManager)
    {
        _worldManager = worldManager;
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

        _pageTableTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32i, PT_X, PT_Y, PT_Z, 0, PixelFormat.RedInteger, PixelType.Int, _cpuPageTable);
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

        _gridHeadTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _gridHeadTexture);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32i, OBJ_GRID_SIZE, OBJ_GRID_SIZE, OBJ_GRID_SIZE, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);

        _linkedListSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _linkedListSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, MAX_LINKED_LIST_NODES * 8, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo);

        _atomicCounterBuffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer);
        GL.BufferData(BufferTarget.AtomicCounterBuffer, sizeof(uint), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, _atomicCounterBuffer);

        _dynamicObjectsBuffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer);
        int maxObjectsBytes = _tempGpuObjectsArray.Length * Marshal.SizeOf<GpuDynamicObject>();
        GL.BufferData(BufferTarget.ShaderStorageBuffer, maxObjectsBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);

        UploadAllVisibleChunks();
    }

    public void Update(float deltaTime)
    {
        _totalTime += deltaTime;

        int unloads = 0;
        while (unloads < 200 && _chunksToUnload.TryDequeue(out var pos)) { ProcessUnload(pos); unloads++; }

        int uploads = 0;
        int limit = GameSettings.GpuUploadSpeed;
        while (uploads < limit && _chunksToUpload.TryDequeue(out var chunk))
        {
            _queuedChunkPositions.Remove(chunk.Position);
            UploadChunkVoxels(chunk);
            uploads++;
        }

        if (_pageTableDirty)
        {
            GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, PT_X, PT_Y, PT_Z, PixelFormat.RedInteger, PixelType.Int, _cpuPageTable);
            _pageTableDirty = false;
        }

        UpdateDynamicObjectsAndGrid();
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

        if (count > 0)
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            Parallel.For(0, count, options, i =>
            {
                var vo = voxelObjects[i];
                Matrix4 model = Matrix4.CreateTranslation(-vo.LocalCenterOfMass) * Matrix4.CreateFromQuaternion(vo.Rotation) * Matrix4.CreateTranslation(vo.Position);
                var col = MaterialRegistry.GetColor(vo.Material);

                // --- ИСПРАВЛЕНИЕ ЗДЕСЬ ---
                // Границы LocalBoundsMin/Max УЖЕ рассчитаны относительно центра масс в VoxelObject.cs.
                // Не нужно добавлять LocalCenterOfMass обратно. Это вызывало смещение.
                _tempGpuObjectsArray[i] = new GpuDynamicObject
                {
                    Model = model,
                    InvModel = Matrix4.Invert(model),
                    Color = new Vector4(col.r, col.g, col.b, 1.0f),
                    BoxMin = new Vector4(vo.LocalBoundsMin, 0 ),
                    BoxMax = new Vector4(vo.LocalBoundsMax, 0)
                };
                // --------------------------
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

        _shader.SetInt("uBoundMinX", camCx - range);
        _shader.SetInt("uBoundMinY", 0);
        _shader.SetInt("uBoundMinZ", camCz - range);
        _shader.SetInt("uBoundMaxX", camCx + range + 1);
        _shader.SetInt("uBoundMaxY", WorldManager.WorldHeightChunks);
        _shader.SetInt("uBoundMaxZ", camCz + range + 1);

        GL.BindImageTexture(0, _pageTableTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32i);
        GL.BindImageTexture(1, _gridHeadTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32i);

        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo);

        _shader.SetTexture("uNoiseTexture", _noiseTexture, TextureUnit.Texture0);
        _shader.SetVector3("uGridOrigin", _lastGridOrigin);
        _shader.SetFloat("uGridStep", _gridCellSize);
        _shader.SetInt("uGridSize", OBJ_GRID_SIZE);
        _shader.SetInt("uObjectCount", _worldManager.GetAllVoxelObjects().Count);

        GL.Disable(EnableCap.DepthTest);
        GL.BindVertexArray(_quadVao);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        GL.Enable(EnableCap.DepthTest);
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

    private int GetPageTableIndex(Vector3i chunkPos) => (chunkPos.X & MASK_X) + PT_X * ((chunkPos.Y & MASK_Y) + PT_Y * (chunkPos.Z & MASK_Z));

    private void ProcessUnload(Vector3i pos)
    {
        if (_loadedChunks.TryGetValue(pos, out int offset))
        {
            _loadedChunks.Remove(pos);
            _freeSlots.Push(offset);
            _cpuPageTable[GetPageTableIndex(pos)] = -1;
            _pageTableDirty = true;
        }
    }

    public void UploadChunkVoxels(Chunk chunk)
    {
        if (chunk == null || !chunk.IsLoaded) return;

        chunk.ReadVoxelsUnsafe(src =>
        {
            if (chunk.SolidCount == 0)
            {
                ProcessUnload(chunk.Position);
                return;
            }

            if (src == null) return;

            int offset;
            if (!_loadedChunks.TryGetValue(chunk.Position, out offset))
            {
                if (!_freeSlots.TryPop(out offset))
                {
                    Console.WriteLine("[Renderer] VRAM Full! Chunk upload deferred.");
                    _chunksToUpload.Enqueue(chunk);
                    return;
                }
                _loadedChunks[chunk.Position] = offset;
            }

            System.Buffer.BlockCopy(src, 0, _chunkUploadBuffer, 0, Constants.ChunkVolume);
            IntPtr gpuOffset = (IntPtr)((long)offset * PackedChunkSizeInInts * sizeof(uint));
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsbo);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, gpuOffset, PackedChunkSizeInInts * sizeof(uint), _chunkUploadBuffer);

            _cpuPageTable[GetPageTableIndex(chunk.Position)] = offset * PackedChunkSizeInInts;
            _pageTableDirty = true;
        });
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

    public void UploadAllVisibleChunks() { foreach (var c in _worldManager.GetChunksSnapshot()) NotifyChunkLoaded(c); }
    public void NotifyChunkLoaded(Chunk chunk) { if (chunk != null && chunk.IsLoaded) { if (_queuedChunkPositions.Add(chunk.Position)) _chunksToUpload.Enqueue(chunk); } }
    public void UnloadChunk(Vector3i chunkPos) => _chunksToUnload.Enqueue(chunkPos);
    public void OnResize(int width, int height) => GL.Viewport(0, 0, width, height);

    public void Dispose()
    {
        GL.DeleteTexture(_pageTableTexture);
        GL.DeleteTexture(_gridHeadTexture);
        GL.DeleteTexture(_noiseTexture);
        GL.DeleteBuffer(_voxelSsbo);
        GL.DeleteBuffer(_dynamicObjectsBuffer);
        GL.DeleteBuffer(_linkedListSsbo);
        GL.DeleteBuffer(_atomicCounterBuffer);
        GL.DeleteVertexArray(_quadVao);
        _shader?.Dispose();
        _gridComputeShader?.Dispose();
    }
}