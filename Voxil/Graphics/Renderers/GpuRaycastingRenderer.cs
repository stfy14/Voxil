using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StbImageSharp;
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
private int _objectGridTexture;     // Динамика: Сетка ускорения (R32I)
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

private readonly int[] _cpuObjectGrid = new int[OBJ_GRID_SIZE * OBJ_GRID_SIZE * OBJ_GRID_SIZE];

// --- Memory Management ---
private int _currentCapacity;
private readonly int[] _cpuPageTable = new int[PT_X * PT_Y * PT_Z];
private bool _pageTableDirty = true;
private readonly int[] _clearPixelData = new int[] { -1 };

private Stack<int> _freeSlots = new Stack<int>();
private Dictionary<Vector3i, int> _loadedChunks = new Dictionary<Vector3i, int>();

private ConcurrentQueue<Chunk> _chunksToUpload = new ConcurrentQueue<Chunk>();
private ConcurrentQueue<Vector3i> _chunksToUnload = new ConcurrentQueue<Vector3i>();
private HashSet<Vector3i> _queuedChunkPositions = new HashSet<Vector3i>();

private readonly uint[] _chunkUploadBuffer = new uint[ChunkVol];
private GpuDynamicObject[] _tempGpuObjectsArray = new GpuDynamicObject[1024]; 

private Stopwatch _stopwatch = new Stopwatch();
private const int PackedChunkSizeInInts = ChunkVol / 4;

// --- АНИМАЦИЯ ---
private float _totalTime = 0f; // Накопитель времени для шейдеров

private int _noiseTexture;

[StructLayout(LayoutKind.Sequential)]
struct GpuDynamicObject
{
    public Matrix4 Model;
    public Matrix4 InvModel;
    public Vector4 Color;
    public Vector4 BoxMin;
    public Vector4 BoxMax;
}

public GpuRaycastingRenderer(WorldManager worldManager)
{
    _worldManager = worldManager;

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
    
    // 4. Page Table (Статика)
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

    // 5. Voxel SSBO (Статика)
    _voxelSsbo = GL.GenBuffer();
    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsbo);
    long totalBytes = (long)_currentCapacity * PackedChunkSizeInInts * sizeof(uint);
    GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)totalBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
    GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);

    // 6. Object Grid (Динамика)
    _objectGridTexture = GL.GenTexture();
    GL.BindTexture(TextureTarget.Texture3D, _objectGridTexture);
    Array.Fill(_cpuObjectGrid, -1);
    GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32i,
        OBJ_GRID_SIZE, OBJ_GRID_SIZE, OBJ_GRID_SIZE, 0,
        PixelFormat.RedInteger, PixelType.Int, _cpuObjectGrid);

    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
    GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToBorder);
    GL.TexParameterI(TextureTarget.Texture3D, TextureParameterName.TextureBorderColor, new int[] { -1, -1, -1, -1 });

    // 7. Dynamic Objects SSBO
    _dynamicObjectsBuffer = GL.GenBuffer();
    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer);
    int maxObjectsBytes = _tempGpuObjectsArray.Length * Marshal.SizeOf<GpuDynamicObject>();
    GL.BufferData(BufferTarget.ShaderStorageBuffer, maxObjectsBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
    GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);

    Console.WriteLine($"[GpuRenderer] Init complete. Voxel VRAM: {totalBytes / 1024 / 1024} MB. Grid Size: {OBJ_GRID_SIZE}^3 (R32I)");

    UploadAllVisibleChunks();
}

public void Update(float deltaTime)
{
    if (PerformanceMonitor.IsEnabled) _stopwatch.Restart();

    // 1. Обновляем глобальное время для шейдеров
    _totalTime += deltaTime;

    int unloads = 0;
    while (unloads < 100 && _chunksToUnload.TryDequeue(out var pos))
    {
        ProcessUnload(pos);
        unloads++;
    }

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

    for (int i = 0; i < count; i++)
    {
        var vo = voxelObjects[i];
        
        // 1. Считаем Model Matrix
        Matrix4 model = Matrix4.CreateTranslation(-vo.LocalCenterOfMass) * 
                        Matrix4.CreateFromQuaternion(vo.Rotation) * 
                        Matrix4.CreateTranslation(vo.Position);

        // 2. Считаем Inverse Model Matrix
        Matrix4 invModel = Matrix4.Invert(model); 

        var col = MaterialRegistry.GetColor(vo.Material);

        _tempGpuObjectsArray[i] = new GpuDynamicObject
        {
            Model = model,
            InvModel = invModel,
            Color = new Vector4(col.r, col.g, col.b, 1.0f),
            BoxMin = new Vector4(vo.LocalBoundsMin + vo.LocalCenterOfMass, 0),
            BoxMax = new Vector4(vo.LocalBoundsMax + vo.LocalCenterOfMass, 0)
        };
    }

    if (count > 0)
    {
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, count * Marshal.SizeOf<GpuDynamicObject>(), _tempGpuObjectsArray);
    }

    // COMPUTE SHADER
    float snap = OBJ_GRID_CELL_SIZE;
    Vector3 snappedCenter = new Vector3(
        (float)Math.Floor(_worldManager.GetPlayerPosition().X / snap) * snap,
        (float)Math.Floor(_worldManager.GetPlayerPosition().Y / snap) * snap,
        (float)Math.Floor(_worldManager.GetPlayerPosition().Z / snap) * snap
    );
    float halfExtent = (OBJ_GRID_SIZE * OBJ_GRID_CELL_SIZE) / 2.0f;
    Vector3 gridOrigin = snappedCenter - new Vector3(halfExtent);
    _lastGridOrigin = gridOrigin;

    int clearVal = -1; 
    GL.ClearTexImage(_objectGridTexture, 0, PixelFormat.RedInteger, PixelType.Int, _clearPixelData);

    if (count > 0)
    {
        _gridComputeShader.Use();
        _gridComputeShader.SetInt("uObjectCount", count);
        _gridComputeShader.SetVector3("uGridOrigin", gridOrigin);
        _gridComputeShader.SetFloat("uGridStep", OBJ_GRID_CELL_SIZE);
        _gridComputeShader.SetInt("uGridSize", OBJ_GRID_SIZE);
        
        GL.BindImageTexture(0, _objectGridTexture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R32i);
        
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);

        int groups = (count + 63) / 64; 
        GL.DispatchCompute(groups, 1, 1);
        
        GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.ShaderImageAccessBarrierBit);
    }
}

public void ReloadShader()
{
    // 1. Удаляем старый шейдер
    _shader?.Dispose();

    // 2. Формируем список Defines
    var defines = new List<string>();
        
    // Вода
    if (GameSettings.UseProceduralWater)
    {
        defines.Add("WATER_MODE_PROCEDURAL");
    }

    // Тени
    switch (GameSettings.CurrentShadowMode)
    {
        case ShadowMode.Hard:
            defines.Add("SHADOW_MODE_HARD");
            break;
        case ShadowMode.Soft:
            defines.Add("SHADOW_MODE_SOFT");
            break;
        // ShadowMode.None - просто не добавляем ничего
    }

    // 3. Компилируем
    try 
    {
        _shader = new Shader("Shaders/raycast.vert", "Shaders/raycast.frag", defines);
        Console.WriteLine($"[Renderer] Shader reloaded. Shadow: {GameSettings.CurrentShadowMode}, Water: {(GameSettings.UseProceduralWater ? "Proc" : "Tex")}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Renderer] Shader compilation failed: {ex.Message}");
    }
}

private Vector3 _lastGridOrigin;

public void Render(Camera camera)
{
    if (PerformanceMonitor.IsEnabled) _stopwatch.Restart();

    _shader.Use();

    _shader.SetVector3("uCamPos", camera.Position);
    _shader.SetMatrix4("uView", camera.GetViewMatrix());
    _shader.SetMatrix4("uProjection", camera.GetProjectionMatrix());
    _shader.SetFloat("uRenderDistance", _worldManager.GetViewRangeInVoxels());
    int maxRaySteps = (int)(_worldManager.GetViewRangeInVoxels() * 3) + 32; 
    _shader.SetInt("uMaxRaySteps", GameSettings.RenderDistance * 4);

    _shader.SetVector3("uSunDir", Vector3.Normalize(new Vector3(0.0f, 0.3f, 0.8f)));
    
    
    // --- ПЕРЕДАЕМ ВРЕМЯ В ШЕЙДЕР ---
    _shader.SetFloat("uTime", _totalTime);
    
    _shader.SetInt("uSoftShadowSamples", GameSettings.SoftShadowSamples);

    Vector3 camPos = camera.Position;
    int camCx = (int)Math.Floor(camPos.X / ChunkSize);
    int camCy = (int)Math.Floor(camPos.Y / ChunkSize);
    int camCz = (int)Math.Floor(camPos.Z / ChunkSize);
    int range = _worldManager.GetViewRangeInVoxels() / ChunkSize;

    _shader.SetInt("uBoundMinX", camCx - range);
    _shader.SetInt("uBoundMinY", 0);
    _shader.SetInt("uBoundMinZ", camCz - range);
    _shader.SetInt("uBoundMaxX", camCx + range + 1);
    _shader.SetInt("uBoundMaxY", WorldManager.WorldHeightChunks);
    _shader.SetInt("uBoundMaxZ", camCz + range + 1);

    // Статика
    GL.BindImageTexture(0, _pageTableTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32i);
    GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);

    // Динамика
    GL.BindImageTexture(1, _objectGridTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32i);
    GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);

    // Биндинг ImageTexture (для вокселей) оставляем как есть, так как это специфично
    GL.BindImageTexture(0, _pageTableTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32i);
    GL.BindImageTexture(1, _objectGridTexture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R32i);
    GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _voxelSsbo);
    GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);

    // --- ВОТ ТУТ СТАЛО ЧИЩЕ ---
    // Мы просто говорим шейдеру: "Возьми текстуру _noiseTexture и положи в слот 0 для переменной uNoiseTexture"
    _shader.SetTexture("uNoiseTexture", _noiseTexture, TextureUnit.Texture0);
    
    _shader.SetVector3("uGridOrigin", _lastGridOrigin);
    _shader.SetFloat("uGridStep", OBJ_GRID_CELL_SIZE);
    _shader.SetInt("uGridSize", OBJ_GRID_SIZE);

    int dynObjectCount = Math.Min(_worldManager.GetAllVoxelObjects().Count, _tempGpuObjectsArray.Length);
    _shader.SetInt("uObjectCount", dynObjectCount);

    GL.BindVertexArray(_quadVao);
    GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

    if (PerformanceMonitor.IsEnabled)
    {
        _stopwatch.Stop();
        PerformanceMonitor.Record(ThreadType.GpuRender, _stopwatch.ElapsedTicks);
    }
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

// Добавляем вспомогательный метод для загрузки
private int LoadTexture(string path)
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"[Error] Texture not found: {path}");
        return 0; // Или выбросить исключение
    }

    int handle = GL.GenTexture();
    GL.BindTexture(TextureTarget.Texture2D, handle);

    // Переворачиваем текстуру, так как в OpenGL Y растет вверх, а в картинках вниз
    StbImage.stbi_set_flip_vertically_on_load(1);

    using (Stream stream = File.OpenRead(path))
    {
        // Загружаем картинку
        ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 
            image.Width, image.Height, 0, 
            PixelFormat.Rgba, PixelType.UnsignedByte, image.Data);
    }

    // ВАЖНЫЕ НАСТРОЙКИ ДЛЯ ВОДЫ
    // Repeat обязателен, чтобы волны не "заканчивались"
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        
    // Linear фильтрация, чтобы волны были плавными, а не пиксельными
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);

    // Генерируем мипмапы, чтобы вода не шумела вдалеке
    GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

    return handle;
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
    GL.DeleteTexture(_noiseTexture);
    GL.DeleteBuffer(_voxelSsbo);
    GL.DeleteBuffer(_dynamicObjectsBuffer);
    GL.DeleteVertexArray(_quadVao);
    _shader?.Dispose();
    _gridComputeShader?.Dispose();
}
}