// --- START OF FILE GpuRaycastingRenderer.cs ---

using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq;
using StbImageSharp;

public class GpuRaycastingRenderer : IDisposable
{
    private readonly ShaderSystem _shaderSystem;
    private Shader _gridComputeShader;
    private Shader _clearGridShader;
    private int _quadVao;
    private readonly IWorldService _worldService;
    private IVoxelObjectService _objectService;
    private VoxelObject _editorModel;
    private bool _isEditorMode = false;

    private int _pageTableTexture;
    private DynSvoManager _svoManager;

    private const int MAX_TOTAL_BANKS = 8;
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

    private int _mainFbo;
    private int _mainDepthTexture;
    private int _renderWidth;
    private int _renderHeight;

    private int _gColorTexture;
    private int _gDataTexture;

    // --- SHADOWS ---
    private int _shadowFbo;
    private int _shadowTexture;
    private int _shadowPointLightTexture;
    private int _shadowWidth;
    private int _shadowHeight;

    private int _shadowFullFbo;
    private int _shadowFullTexture;
    private int _shadowPointLightFullTexture;

    private int _compositeFbo;
    private int _mainColorTexture;

    // --- НОВОЕ: БУФЕР ДЛЯ DEBUG РЕНДЕРА ---
    private int _debugFbo;
    private int _debugColorTexture;

    private Shader _taaShader;
    private int[] _historyFbo = new int[2];
    private int[] _historyTexture = new int[2];
    private int _historyWriteIndex = 0;
    private long _frameIndex = 0;
    private Matrix4 _prevCleanViewProjection = Matrix4.Identity;
    private Vector2 _prevJitterNDC = Vector2.Zero;
    private bool _resetTaaHistory = true;

    private VoxelObject _currentViewModel;

    private readonly Vector2[] _haltonSequence = new Vector2[]
    {
        new(0.5f, 0.333333f), new(0.25f, 0.666667f), new(0.75f, 0.111111f), new(0.125f, 0.444444f),
        new(0.625f, 0.777778f), new(0.375f, 0.222222f), new(0.875f, 0.555556f), new(0.0625f, 0.888889f)
    };

    private int _editSsbo;
    private ConcurrentQueue<GpuVoxelEdit> _editQueue = new ConcurrentQueue<GpuVoxelEdit>();
    private GpuVoxelEdit[] _editUploadArray = new GpuVoxelEdit[1024];

    private VCTSystem _vctSystem;
    private int _pointLightSsbo;
    private const int POINT_LIGHT_BINDING = 18;
    private int _lastPointLightCount = 0; [StructLayout(LayoutKind.Sequential)]
    private struct GpuPointLight
    {
        public Vector4 PosRadius;
        public Vector4 ColorIntensity;
    }
    private readonly GpuPointLight[] _pointLightCache = new GpuPointLight[32];

    public VCTSystem VCTSystem => _vctSystem;

    public event Action OnShaderReloaded
    {
        add => _shaderSystem.OnShaderReloaded += value;
        remove => _shaderSystem.OnShaderReloaded -= value;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GpuVoxelEdit
    {
        public uint ChunkSlot;
        public uint VoxelIndex;
        public uint NewMaterial;
        public uint Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GpuDynamicObject
    {
        public Matrix4 Model;
        public Matrix4 InvModel;
        public Vector4 Color;
        public Vector4 BoxMin;
        public Vector4 BoxMax;
        public uint SvoOffset;
        public uint GridSize;
        public float VoxelSize;
        public uint Padding;
    }

    public GpuRaycastingRenderer(IWorldService worldService)
    {
        _worldService = worldService;
        _shaderSystem = new ShaderSystem();
        _gridCellSize = Math.Max(Constants.VoxelSize * 16.0f, 2.0f);
        TotalVramMb = DetectTotalVram();
    }

    public void Load()
    {
        _objectService = ServiceLocator.Get<IVoxelObjectService>();
        _gridComputeShader = new Shader(ShaderPaths.GridUpdate);
        _clearGridShader = new Shader(ShaderPaths.ClearGrid);
        _taaShader = new Shader(ShaderPaths.RaycastVert, ShaderPaths.TaaFrag);

        _svoManager = new DynSvoManager();
        _svoManager.Initialize();
        _quadVao = GL.GenVertexArray();

        GL.BindVertexArray(_quadVao);
        int dummy = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, dummy);
        GL.BufferData(BufferTarget.ArrayBuffer, sizeof(float) * 8,
            new float[] { -1, -1, 1, -1, -1, 1, 1, 1 }, BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        _noiseTexture = LoadTexture(ShaderPaths.Textures.WaterNoise);

        InitializeBuffers();
        UploadAllVisibleChunks();
    }

    public void SetEditorModel(VoxelObject model)
    {
        _editorModel = model;
        _isEditorMode = model != null;
        _currentViewModel = null;
        GameSettings.IsEditorMode = _isEditorMode;
        _shaderSystem.Compile(MAX_TOTAL_BANKS, _chunksPerBank);

        if (model != null)
        {
            model.SvoDirty = true;
            model.SvoGpuOffset = uint.MaxValue;
            model.SvoGridSize = 0;
        }
    }

    public void SetHoverVoxel(Vector3 min, Vector3 max)
    {
        var shader = _shaderSystem.RaycastShader;
        if (shader == null) return;
        shader.Use();
        shader.SetVector3("uHoverVoxelMin", min);
        shader.SetVector3("uHoverVoxelMax", max);
    }

    private int DetectTotalVram()
    {
        try { GL.GetInteger((GetPName)0x9048, out int kb); if (kb > 0) return kb / 1024; } catch { }
        return 4096;
    }

    public void SetViewModel(VoxelObject viewModel) => _currentViewModel = viewModel;
    public void RequestReallocation() { CleanupBuffers(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); _reallocationPending = true; }
    public bool IsReallocationPending() => _reallocationPending;
    public void PerformReallocation() { if (!_reallocationPending) return; InitializeBuffers(); _reallocationPending = false; }
    public long CalculateMemoryBytesForDistance(int distance)
    {
        int chunks = (distance * 2 + 1) * (distance * 2 + 1) * WorldManager.WorldHeightChunks;
        return (long)chunks * (PackedChunkSizeInInts * 4 + ChunkMaskSizeInUlongs * 8);
    }

    public void ApplyRenderScale() => OnResize(_windowWidth, _windowHeight);

    public void InitializeBuffers()
    {
        if (_dummySSBO == 0)
        {
            _dummySSBO = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dummySSBO);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, 4, IntPtr.Zero, BufferUsageHint.StaticDraw);
        }

        CleanupBanks();
        if (!TryAddBank()) throw new Exception("Critical: Failed to allocate even the first VRAM bank!");

        _chunksPerBank = (int)(BANK_SIZE_BYTES / (PackedChunkSizeInInts * 4));

        BindAllBuffers();

        long maxChunksForMasks = 262144;
        long mBytes = maxChunksForMasks * ChunkMaskSizeInUlongs * 8;

        if (_maskSsbo == 0) _maskSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _maskSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)mBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo);

        if (_pageTableTexture == 0) _pageTableTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32ui, PT_X, PT_Y, PT_Z, 0,
            PixelFormat.RedInteger, PixelType.UnsignedInt, _cpuPageTable);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);

        CreateAuxBuffers();

        if (_pointLightSsbo == 0)
        {
            _pointLightSsbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _pointLightSsbo);
            GL.BufferData(BufferTarget.ShaderStorageBuffer,
                32 * Marshal.SizeOf<GpuPointLight>(), IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, POINT_LIGHT_BINDING, _pointLightSsbo);
        }

        _shaderSystem.Compile(MAX_TOTAL_BANKS, _chunksPerBank);
        ResetAllocationLogic();

        _shaderSystem.OnShaderReloaded -= ReinitVCT;
        _shaderSystem.OnShaderReloaded += ReinitVCT;
        ReinitVCT();
    }

    private void ReinitVCT()
    {
        _vctSystem?.Dispose();
        _vctSystem = null;
        if (GameSettings.EnableGI && _shaderSystem.VctClipmapBuildShader != null)
        {
            _vctSystem = new VCTSystem(_shaderSystem.VctClipmapBuildShader);
        }
    }

    public void OnResize(int w, int h)
    {
        _windowWidth = w; _windowHeight = h;
        _renderWidth = Math.Max(1, (int)(w * GameSettings.RenderScale));
        _renderHeight = Math.Max(1, (int)(h * GameSettings.RenderScale));
        _beamWidth = Math.Max(1, _renderWidth / BeamDivisor);
        _beamHeight = Math.Max(1, _renderHeight / BeamDivisor);

        if (_beamTexture != 0) GL.DeleteTexture(_beamTexture);
        _beamTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _beamTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R32f, _beamWidth, _beamHeight, 0, PixelFormat.Red, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _beamFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _beamTexture, 0);

        if (_mainFbo != 0) GL.DeleteFramebuffer(_mainFbo);
        _mainFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _mainFbo);

        if (_gColorTexture != 0) GL.DeleteTexture(_gColorTexture);
        _gColorTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _gColorTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, _renderWidth, _renderHeight, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _gColorTexture, 0);

        if (_gDataTexture != 0) GL.DeleteTexture(_gDataTexture);
        _gDataTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _gDataTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, _renderWidth, _renderHeight, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, _gDataTexture, 0);

        if (_mainDepthTexture != 0) GL.DeleteTexture(_mainDepthTexture);
        _mainDepthTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _mainDepthTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, _renderWidth, _renderHeight, 0, PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _mainDepthTexture, 0);
        GL.DrawBuffers(2, new DrawBuffersEnum[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1 });

        // --- SHADOW DOWNSCALE BUFFERS ---
        _shadowWidth = Math.Max(1, _renderWidth / GameSettings.ShadowDownscale);
        _shadowHeight = Math.Max(1, _renderHeight / GameSettings.ShadowDownscale);

        if (_shadowFbo != 0) GL.DeleteFramebuffer(_shadowFbo);
        _shadowFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);

        if (_shadowTexture != 0) GL.DeleteTexture(_shadowTexture);
        _shadowTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _shadowTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rg16f, _shadowWidth, _shadowHeight, 0, PixelFormat.Rg, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _shadowTexture, 0);

        if (_shadowPointLightTexture != 0) GL.DeleteTexture(_shadowPointLightTexture);
        _shadowPointLightTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _shadowPointLightTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, _shadowWidth, _shadowHeight, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, _shadowPointLightTexture, 0);

        GL.DrawBuffers(2, new DrawBuffersEnum[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1 });

        // --- SHADOW UPSAMPLE BUFFERS ---
        if (_shadowFullFbo != 0) GL.DeleteFramebuffer(_shadowFullFbo);
        _shadowFullFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFullFbo);

        if (_shadowFullTexture != 0) GL.DeleteTexture(_shadowFullTexture);
        _shadowFullTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _shadowFullTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rg16f, _renderWidth, _renderHeight, 0, PixelFormat.Rg, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _shadowFullTexture, 0);

        if (_shadowPointLightFullTexture != 0) GL.DeleteTexture(_shadowPointLightFullTexture);
        _shadowPointLightFullTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _shadowPointLightFullTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, _renderWidth, _renderHeight, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, _shadowPointLightFullTexture, 0);

        GL.DrawBuffers(2, new DrawBuffersEnum[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1 });

        // --- COMPOSITE & TAA ---
        if (_mainColorTexture != 0) GL.DeleteTexture(_mainColorTexture);
        _mainColorTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _mainColorTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, _renderWidth, _renderHeight, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);

        if (_compositeFbo != 0) GL.DeleteFramebuffer(_compositeFbo);
        _compositeFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _compositeFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _mainColorTexture, 0);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _mainDepthTexture, 0);
        GL.DrawBuffers(1, new DrawBuffersEnum[] { DrawBuffersEnum.ColorAttachment0 });

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

        // --- БУФЕР ДЛЯ ДЕБАГА ---
        if (_debugColorTexture != 0) GL.DeleteTexture(_debugColorTexture);
        _debugColorTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _debugColorTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, _renderWidth, _renderHeight, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

        if (_debugFbo != 0) GL.DeleteFramebuffer(_debugFbo);
        _debugFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _debugFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _debugColorTexture, 0);
        // ПРИВЯЗЫВАЕМ ГЛУБИНУ ОТ РЕЙКАСТЕРА К ДЕБАГ ФБО!
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _mainDepthTexture, 0);


        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, _windowWidth, _windowHeight);
        _resetTaaHistory = true;
        _frameIndex = 0;
    }

    public void Render(CameraData cam)
    {
        if (_reallocationPending || _activeBankCount == 0) return;

        _frameIndex++;
        int index = (int)(_frameIndex % 8);
        Vector2 halton = _haltonSequence[index];
        Vector2 jitter = (halton - new Vector2(0.5f));

        Matrix4 view = cam.View;
        Matrix4 cleanProj = cam.Projection;
        Matrix4 cleanViewProj = view * cleanProj;
        bool useTaa = GameSettings.EnableTAA;
        if (_resetTaaHistory) useTaa = false;
        Matrix4 activeProj = useTaa ? cam.JitteredProjection : cleanProj;

        _shaderSystem.Use();
        var shader = _shaderSystem.RaycastShader;
        if (shader == null) return;

        shader.SetVector3("uCamPos", cam.Position);
        shader.SetMatrix4("uView", view);
        shader.SetMatrix4("uProjection", activeProj);
        shader.SetMatrix4("uInvProjection", Matrix4.Invert(activeProj));
        shader.SetMatrix4("uInvView", Matrix4.Invert(view));
        shader.SetMatrix4("uCleanProjection", cleanProj);
        shader.SetInt("uShowDebugHeatmap", GameSettings.ShowDebugHeatmap ? 1 : 0);

        float viewRange = _worldService.GetViewRangeInMeters();
        shader.SetFloat("uRenderDistance", viewRange);
        shader.SetFloat("uLodDistance", GameSettings.EnableLOD ? viewRange * GameSettings.LodPercentage : 100000.0f);
        shader.SetInt("uDisableEffectsOnLOD", GameSettings.DisableEffectsOnLOD ? 1 : 0);
        shader.SetInt("uMaxRaySteps", (int)(GameSettings.RenderDistance * 8) + 2028);

        float sunAngle = ((float)GameSettings.TotalTimeHours - 6.0f) / 24.0f * MathHelper.TwoPi;
        float sunX = (float)Math.Cos(sunAngle);
        float sunY = (float)Math.Sin(sunAngle);
        float sunZ = 0.3f;

        float moonPhaseOffset = ((float)GameSettings.TotalTimeHours / (24.0f * 28.0f)) * MathHelper.TwoPi;
        float moonAngle = sunAngle - MathHelper.Pi - moonPhaseOffset;
        float moonX = (float)Math.Cos(moonAngle);
        float moonY = (float)Math.Sin(moonAngle);
        float moonZ = -0.1f;

        Vector3 sunDir = Vector3.Normalize(new Vector3(sunX, sunY, sunZ));
        Vector3 moonDir = Vector3.Normalize(new Vector3(moonX, moonY, moonZ));

        shader.SetVector3("uSunDir", sunDir);
        shader.SetVector3("uMoonDir", moonDir);

        if (_isEditorMode)
        {
            shader.SetInt("uBoundMinX", 0); shader.SetInt("uBoundMinY", 0); shader.SetInt("uBoundMinZ", 0);
            shader.SetInt("uBoundMaxX", 0); shader.SetInt("uBoundMaxY", 0); shader.SetInt("uBoundMaxZ", 0);
        }
        else
        {
            Vector3 p = cam.Position;
            int sz = Constants.ChunkSizeWorld;
            int cx = (int)Math.Floor(p.X / sz), cy = (int)Math.Floor(p.Y / sz), cz = (int)Math.Floor(p.Z / sz);
            int r = GameSettings.RenderDistance + 2;
            shader.SetInt("uBoundMinX", cx - r); shader.SetInt("uBoundMinY", 0); shader.SetInt("uBoundMinZ", cz - r);
            shader.SetInt("uBoundMaxX", cx + r); shader.SetInt("uBoundMaxY", WorldManager.WorldHeightChunks); shader.SetInt("uBoundMaxZ", cz + r);
        }

        GL.ActiveTexture(TextureUnit.Texture6);
        GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
        shader.SetInt("uPageTable", 6);

        GL.ActiveTexture(TextureUnit.Texture7);
        GL.BindTexture(TextureTarget.Texture3D, _gridHeadTexture);
        shader.SetInt("uObjectGridHead", 7);

        BindAllBuffers();
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo);
        _svoManager.Bind();
        shader.SetTexture("uNoiseTexture", _noiseTexture, TextureUnit.Texture0);
        shader.SetVector3("uGridOrigin", _lastGridOrigin);
        shader.SetFloat("uGridStep", _gridCellSize);
        shader.SetInt("uGridSize", OBJ_GRID_SIZE);
        int objCount = _objectService.GetAllVoxelObjects().Count
                     + (_currentViewModel != null ? 1 : 0)
                     + (_editorModel != null ? 1 : 0);
        shader.SetInt("uObjectCount", objCount);
        shader.SetInt("uSoftShadowSamples", GameSettings.SoftShadowSamples);
        shader.SetFloat("uTime", _totalTime);

        GL.BindVertexArray(_quadVao);

        UpdatePointLights();

        if (_vctSystem != null && GameSettings.EnableGI && _frameIndex > 15 && cam.Position.Y > -1000.0f)
        {
            // Убедимся, что текстура мира и банки привязаны для compute-шейдера VCT
            GL.ActiveTexture(TextureUnit.Texture6);
            GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
            BindAllBuffers();
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo);

            // Радиус VCT: L2 покрывает 1024м, берем с запасом 18 чанков (~1152м)
            int vctChunkRadius = 18;
            int cx = (int)Math.Floor(cam.Position.X / Constants.ChunkSizeWorld);
            int cz = (int)Math.Floor(cam.Position.Z / Constants.ChunkSizeWorld);
            int maxSteps = (int)(viewRange * 8) + 512;

            _vctSystem.Update(cam.Position, sunDir,
                            cx - vctChunkRadius, 0, cz - vctChunkRadius,
                            cx + vctChunkRadius, WorldManager.WorldHeightChunks, cz + vctChunkRadius,
                            maxSteps,
                            _lastGridOrigin, _gridCellSize, OBJ_GRID_SIZE, objCount);

            // Возвращаем основной шейдер перед рендером
            _shaderSystem.Use();
            GL.BindVertexArray(_quadVao);
        }

        if (GameSettings.BeamOptimization && !_isEditorMode)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _beamFbo);
            GL.Viewport(0, 0, _beamWidth, _beamHeight);
            GL.ClearBuffer(ClearBuffer.Color, 0, new float[] { 10000.0f, 0.0f, 0.0f, 1.0f });
            shader.SetInt("uIsBeamPass", 1);
            GL.Disable(EnableCap.DepthTest);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _mainFbo);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0.0f, 0.0f, 0.0f, 0.0f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        shader.SetInt("uIsBeamPass", 0);
        shader.SetTexture("uBeamTexture", _beamTexture, TextureUnit.Texture1);
        GL.Enable(EnableCap.DepthTest);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        if (!_isEditorMode)
        {
            var shadowShader = _shaderSystem.ShadowShader;
            if (shadowShader != null)
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
                GL.Viewport(0, 0, _shadowWidth, _shadowHeight);
                GL.Disable(EnableCap.DepthTest);

                shadowShader.Use();
                shadowShader.SetInt("uShadowDownscale", GameSettings.ShadowDownscale);
                shadowShader.SetVector3("uCamPos", cam.Position);
                shadowShader.SetMatrix4("uView", view);
                shadowShader.SetMatrix4("uProjection", activeProj);
                shadowShader.SetMatrix4("uInvProjection", Matrix4.Invert(activeProj));
                shadowShader.SetMatrix4("uInvView", Matrix4.Invert(view));
                shadowShader.SetVector3("uSunDir", sunDir);
                shadowShader.SetVector3("uMoonDir", moonDir);
                shadowShader.SetFloat("uTime", _totalTime);
                shadowShader.SetFloat("uRenderDistance", viewRange);
                shadowShader.SetFloat("uLodDistance", GameSettings.EnableLOD
                    ? viewRange * GameSettings.LodPercentage : 100000.0f);
                shadowShader.SetInt("uDisableEffectsOnLOD", GameSettings.DisableEffectsOnLOD ? 1 : 0);
                shadowShader.SetInt("uMaxRaySteps", (int)(GameSettings.RenderDistance * 8) + 2028);
                shadowShader.SetInt("uSoftShadowSamples", GameSettings.SoftShadowSamples);
                shadowShader.SetInt("uObjectCount", objCount);
                shadowShader.SetInt("uShowDebugHeatmap", 0);

                if (_isEditorMode)
                {
                    shadowShader.SetInt("uBoundMinX", 0); shadowShader.SetInt("uBoundMinY", 0); shadowShader.SetInt("uBoundMinZ", 0);
                    shadowShader.SetInt("uBoundMaxX", 0); shadowShader.SetInt("uBoundMaxY", 0); shadowShader.SetInt("uBoundMaxZ", 0);
                }
                else
                {
                    Vector3 p = cam.Position;
                    int sz = Constants.ChunkSizeWorld;
                    int cx = (int)Math.Floor(p.X / sz), cz = (int)Math.Floor(p.Z / sz);
                    int r = GameSettings.RenderDistance + 2;
                    shadowShader.SetInt("uBoundMinX", cx - r); shadowShader.SetInt("uBoundMinY", 0); shadowShader.SetInt("uBoundMinZ", cz - r);
                    shadowShader.SetInt("uBoundMaxX", cx + r); shadowShader.SetInt("uBoundMaxY", WorldManager.WorldHeightChunks); shadowShader.SetInt("uBoundMaxZ", cz + r);
                }

                shadowShader.SetTexture("uGColor", _gColorTexture, TextureUnit.Texture8);
                shadowShader.SetTexture("uGData", _gDataTexture, TextureUnit.Texture9);

                GL.ActiveTexture(TextureUnit.Texture6);
                GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
                shadowShader.SetInt("uPageTable", 6);

                GL.ActiveTexture(TextureUnit.Texture7);
                GL.BindTexture(TextureTarget.Texture3D, _gridHeadTexture);
                shadowShader.SetInt("uObjectGridHead", 7);

                BindAllBuffers();
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo);
                _svoManager.Bind();
                shadowShader.SetTexture("uNoiseTexture", _noiseTexture, TextureUnit.Texture0);
                shadowShader.SetVector3("uGridOrigin", _lastGridOrigin);
                shadowShader.SetFloat("uGridStep", _gridCellSize);
                shadowShader.SetInt("uGridSize", OBJ_GRID_SIZE);

                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, POINT_LIGHT_BINDING, _pointLightSsbo);
                shadowShader.SetInt("uPointLightCount", _lastPointLightCount);

                if (_vctSystem != null && GameSettings.EnableGI)
                {
                    _vctSystem.SetSamplingUniforms(shadowShader);
                }

                GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            }
        }
        else
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
            GL.Viewport(0, 0, _shadowWidth, _shadowHeight);
            GL.ClearBuffer(ClearBuffer.Color, 0, new float[] { 1.0f, 1.0f, 0.0f, 0.0f });
            GL.ClearBuffer(ClearBuffer.Color, 1, new float[] { 0.0f, 0.0f, 0.0f, 0.0f });
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFullFbo);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.Disable(EnableCap.DepthTest);

        var upsampleShader = _shaderSystem.ShadowUpsampleShader;
        upsampleShader.Use();
        upsampleShader.SetInt("uShadowDownscale", GameSettings.ShadowDownscale);
        upsampleShader.SetTexture("uShadowHalfRes", _shadowTexture, TextureUnit.Texture0);
        upsampleShader.SetTexture("uPointLightHalfRes", _shadowPointLightTexture, TextureUnit.Texture1);
        upsampleShader.SetTexture("uGData", _gDataTexture, TextureUnit.Texture2);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _compositeFbo);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.Disable(EnableCap.DepthTest);

        var compositeShader = _shaderSystem.CompositeShader;
        compositeShader.Use();
        compositeShader.SetVector3("uCamPos", cam.Position);
        compositeShader.SetMatrix4("uInvView", Matrix4.Invert(view));
        compositeShader.SetMatrix4("uInvProjection", Matrix4.Invert(activeProj));
        compositeShader.SetVector3("uSunDir", sunDir);
        compositeShader.SetVector3("uMoonDir", moonDir);
        compositeShader.SetFloat("uTime", _totalTime);
        compositeShader.SetFloat("uRenderDistance", viewRange);

        compositeShader.SetTexture("uGColor", _gColorTexture, TextureUnit.Texture0);
        compositeShader.SetTexture("uGData", _gDataTexture, TextureUnit.Texture1);
        compositeShader.SetTexture("uShadowFull", _shadowFullTexture, TextureUnit.Texture2);
        compositeShader.SetTexture("uPointLightFull", _shadowPointLightFullTexture, TextureUnit.Texture3);

        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

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
            _taaShader.SetTexture("uCurrentColorTexture", _mainColorTexture, TextureUnit.Texture0);
            _taaShader.SetTexture("uCurrentDepthTexture", _mainDepthTexture, TextureUnit.Texture1);
            _taaShader.SetTexture("uHistoryTexture", _historyTexture[readIndex], TextureUnit.Texture2);
            _taaShader.SetMatrix4("uInvViewProj", Matrix4.Invert(cleanViewProj));
            _taaShader.SetMatrix4("uPrevViewProj", _prevCleanViewProjection);

            GL.BindVertexArray(_quadVao);

            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }
        else
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _compositeFbo);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _historyFbo[_historyWriteIndex]);
            GL.BlitFramebuffer(0, 0, _renderWidth, _renderHeight, 0, 0, _renderWidth, _renderHeight,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _historyFbo[1 - _historyWriteIndex]);
            GL.BlitFramebuffer(0, 0, _renderWidth, _renderHeight, 0, 0, _renderWidth, _renderHeight,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            if (GameSettings.EnableTAA) _resetTaaHistory = false;
        }

        // КОПИРУЕМ ФИНАЛЬНУЮ КАРТИНКУ В БУФЕР ДЛЯ ДЕБАГА (И НЕ ТРОГАЕМ ЕГО ГЛУБИНУ)
        int finalSrcFbo = (useTaa && !_resetTaaHistory) ? _historyFbo[_historyWriteIndex] : _compositeFbo;
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, finalSrcFbo);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _debugFbo);
        GL.BlitFramebuffer(0, 0, _renderWidth, _renderHeight, 0, 0, _renderWidth, _renderHeight,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.BindVertexArray(0);

        _prevCleanViewProjection = cleanViewProj;
        _prevJitterNDC = useTaa
            ? new Vector2((jitter.X / _renderWidth) * 2.0f, (jitter.Y / _renderHeight) * 2.0f)
            : Vector2.Zero;
        _historyWriteIndex = 1 - _historyWriteIndex;
    }

    // --- НОВОЕ: МЕТОДЫ ДЛЯ DEBUG РЕНДЕРА (С ИДЕАЛЬНЫМ Z-BUFFER) ---
    public void BeginDebugPass()
    {
        // Биндим FBO с привязанным Depth Buffer от рейкастера!
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _debugFbo);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        // Мы НЕ очищаем буферы, мы рисуем поверх уже готовой картинки.
    }

    public void EndDebugPass()
    {
        // Копируем картинку с линиями из _debugFbo на реальный экран (0)
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _debugFbo);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        GL.Viewport(0, 0, _windowWidth, _windowHeight);

        GL.BlitFramebuffer(0, 0, _renderWidth, _renderHeight, 0, 0, _windowWidth, _windowHeight,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void UpdatePointLights()
    {
        if (_pointLightSsbo == 0) return;

        var objects = _objectService.GetAllVoxelObjects();
        int count = 0;
        float alpha = _worldService.PhysicsWorld.PhysicsAlpha;

        void AddLightFromObject(VoxelObject vo, bool isViewModel)
        {
            if (count >= 32) return;
            if (!MaterialRegistry.IsEmissive(vo.Material)) return;
            if (vo.VoxelCoordinates.Count == 0) return;

            var pos = isViewModel ? vo.Position : Vector3.Lerp(vo.PrevPosition, vo.Position, alpha);
            var (r, g, b) = MaterialRegistry.GetColor(vo.Material);

            _pointLightCache[count++] = new GpuPointLight
            {
                PosRadius = new Vector4(pos.X, pos.Y, pos.Z, MaterialRegistry.GetEmissiveRadius(vo.Material)),
                ColorIntensity = new Vector4(r, g, b, MaterialRegistry.GetEmissiveIntensity(vo.Material)),
            };
        }

        foreach (var vo in objects) AddLightFromObject(vo, false);
        if (_currentViewModel != null) AddLightFromObject(_currentViewModel, true);

        _lastPointLightCount = count;

        if (count > 0)
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _pointLightSsbo);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
                count * Marshal.SizeOf<GpuPointLight>(), _pointLightCache);
        }
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
                lock (_chunksPendingUpload) { if (_chunksPendingUpload.Add(chunk.Position)) _uploadQueue.Enqueue(chunk); }
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
                lock (_chunksPendingUpload) { if (_chunksPendingUpload.Add(chunk.Position)) _uploadQueue.Enqueue(chunk); }
            }
        }
    }

    public void UnloadChunk(Vector3i pos)
    {
        if (_allocatedChunks.TryGetValue(pos, out int slot))
        {
            if (slot != SOLITARY_SLOT_INDEX) _freeSlots.Enqueue(slot);
            _allocatedChunks.Remove(pos);
            _cpuPageTable[GetPageTableIndex(pos)] = 0xFFFFFFFF;
            _pageTableDirty = true;
            lock (_chunksPendingUpload) _chunksPendingUpload.Remove(pos);
        }
    }

    public void NotifyVoxelEdited(Chunk chunk, Vector3i localPos, MaterialType newMat)
    {
        if (_allocatedChunks.TryGetValue(chunk.Position, out int slot))
        {
            if (slot == SOLITARY_SLOT_INDEX)
            {
                NotifyChunkLoaded(chunk);
            }
            else
            {
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

    public void UpdateChunkData(float deltaTime)
    {
        _totalTime += deltaTime;

        int editsToApply = Math.Min(_editQueue.Count, 1024);
        if (editsToApply > 0)
        {
            for (int i = 0; i < editsToApply; i++) _editQueue.TryDequeue(out _editUploadArray[i]);

            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _editSsbo);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, editsToApply * 16, _editUploadArray);

            var editShader = _shaderSystem.EditUpdaterShader;
            if (editShader != null)
            {
                editShader.Use();
                editShader.SetInt("uEditCount", editsToApply);
                BindAllBuffers();
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _maskSsbo);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, _editSsbo);
                GL.DispatchCompute((editsToApply + 63) / 64, 1, 1);
                GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
            }
        }

        int limit = GameSettings.GpuUploadSpeed;
        while (limit > 0 && _uploadQueue.TryDequeue(out var chunk))
        {
            if (_allocatedChunks.TryGetValue(chunk.Position, out int slot))
            {
                lock (_chunksPendingUpload) _chunksPendingUpload.Remove(chunk.Position);
                if (chunk.IsLoaded && slot != SOLITARY_SLOT_INDEX) { UploadChunkVoxels(chunk, slot); limit--; }
            }
        }

        if (_pageTableDirty)
        {
            GL.BindTexture(TextureTarget.Texture3D, _pageTableTexture);
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, PT_X, PT_Y, PT_Z,
                PixelFormat.RedInteger, PixelType.UnsignedInt, _cpuPageTable);
            _pageTableDirty = false;
        }
    }

    public void UpdateDynamicObjectsAndGrid(Vector3 gridCenter)
        => UpdateDynamicObjectsAndGridInternal(gridCenter);

    public void UpdateDynamicObjectsAndGrid()
        => UpdateDynamicObjectsAndGridInternal(_worldService.GetPlayerPosition());

    private void UpdateDynamicObjectsAndGridInternal(Vector3 gridCenter)
    {
        var voxelObjects = _objectService.GetAllVoxelObjects();

        int totalCount = voxelObjects.Count;
        if (_currentViewModel != null) totalCount++;
        if (_editorModel != null) totalCount++;

        var svoViewModel = _editorModel ?? _currentViewModel;
        _svoManager.Update(voxelObjects, svoViewModel);

        if (totalCount > _tempGpuObjectsArray.Length)
        {
            Array.Resize(ref _tempGpuObjectsArray, totalCount + 1024);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer);
            GL.BufferData(BufferTarget.ShaderStorageBuffer,
                _tempGpuObjectsArray.Length * Marshal.SizeOf<GpuDynamicObject>(),
                IntPtr.Zero, BufferUsageHint.DynamicDraw);
        }

        int currentIndex = 0;
        float alpha = _worldService.PhysicsWorld.PhysicsAlpha;

        for (int i = 0; i < voxelObjects.Count; i++)
        {
            FillGpuObject(ref _tempGpuObjectsArray[currentIndex], voxelObjects[i], alpha, false);
            currentIndex++;
        }

        if (_currentViewModel != null)
        {
            FillGpuObject(ref _tempGpuObjectsArray[currentIndex], _currentViewModel, 1.0f, true);
            currentIndex++;
        }

        if (_editorModel != null)
        {
            FillGpuObject(ref _tempGpuObjectsArray[currentIndex], _editorModel, 1.0f, false);
            currentIndex++;
        }

        if (totalCount > 0)
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
                totalCount * Marshal.SizeOf<GpuDynamicObject>(), _tempGpuObjectsArray);

            float snap = _gridCellSize;
            Vector3 playerPos = gridCenter;
            Vector3 snappedCenter = new Vector3(
                (float)Math.Floor(playerPos.X / snap) * snap,
                (float)Math.Floor(playerPos.Y / snap) * snap,
                (float)Math.Floor(playerPos.Z / snap) * snap);
            float halfExtent = (OBJ_GRID_SIZE * _gridCellSize) / 2.0f;
            _lastGridOrigin = snappedCenter - new Vector3(halfExtent);

            uint zero = 0;
            GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer);
            GL.BufferSubData(BufferTarget.AtomicCounterBuffer, IntPtr.Zero, sizeof(uint), ref zero);

            _clearGridShader.Use();
            GL.BindImageTexture(1, _gridHeadTexture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R32i);
            GL.DispatchCompute(8, 8, 8);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);

            _gridComputeShader.Use();
            _gridComputeShader.SetInt("uObjectCount", totalCount);
            _gridComputeShader.SetVector3("uGridOrigin", _lastGridOrigin);
            _gridComputeShader.SetFloat("uGridStep", _gridCellSize);
            _gridComputeShader.SetInt("uGridSize", OBJ_GRID_SIZE);
            GL.BindImageTexture(1, _gridHeadTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.R32i);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo);
            GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, _atomicCounterBuffer);
            GL.DispatchCompute((totalCount + 63) / 64, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit |
                             MemoryBarrierFlags.AtomicCounterBarrierBit |
                             MemoryBarrierFlags.ShaderStorageBarrierBit);
        }
    }

    private void FillGpuObject(ref GpuDynamicObject gpuObj, VoxelObject vo, float alpha, bool isViewModel)
    {
        Matrix4 model;
        if (isViewModel)
        {
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
        gpuObj.BoxMin = new Vector4(vo.LocalBoundsMin - new Vector3(0.01f), 0);
        gpuObj.BoxMax = new Vector4(vo.LocalBoundsMax + new Vector3(0.01f), 0);
        gpuObj.SvoOffset = vo.SvoGpuOffset;
        gpuObj.GridSize = (uint)vo.SvoGridSize;
        gpuObj.VoxelSize = vo.SvoVoxelWorldSize;
        gpuObj.Padding = 0u;
    }

    private void ResetAllocationLogic()
    {
        _freeSlots.Clear();
        _allocatedChunks.Clear();
        int chunksPerBank = (int)(BANK_SIZE_BYTES / (PackedChunkSizeInInts * 4));
        for (int b = 0; b < _activeBankCount; b++)
        {
            int start = b * chunksPerBank;
            for (int i = 0; i < chunksPerBank; i++) _freeSlots.Enqueue(start + i);
        }
        Array.Fill(_cpuPageTable, 0xFFFFFFFF);
        _chunksPendingUpload.Clear();
        while (!_uploadQueue.IsEmpty) _uploadQueue.TryDequeue(out _);
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
            GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)BANK_SIZE_BYTES, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            if (GL.GetError() == ErrorCode.OutOfMemory) { GL.DeleteBuffer(newBuffer); Console.WriteLine("[VRAM] Out Of Memory!"); return false; }
            _voxelSsboBanks[newIndex] = newBuffer;
            _activeBankCount++;
            CurrentAllocatedBytes += BANK_SIZE_BYTES;
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 8 + newIndex, newBuffer);
            Console.WriteLine($"[VRAM] Added Bank #{newIndex} (Binding {8 + newIndex}). Total: {_activeBankCount}");
            return true;
        }
        catch { return false; }
    }

    private int GetSlotOrPanic()
    {
        if (_freeSlots.Count > 0) return _freeSlots.Dequeue();

        if (TryAddBank())
        {
            int startSlotIndex = (_activeBankCount - 1) * _chunksPerBank;
            for (int i = 0; i < _chunksPerBank; i++) _freeSlots.Enqueue(startSlotIndex + i);
            Console.WriteLine($"[VRAM] Bank expanded. New free slots: {_freeSlots.Count}");
            return _freeSlots.Dequeue();
        }

        Console.WriteLine("[VRAM] PANIC: GPU Memory Full! Reducing Render Distance!");
        GameSettings.RenderDistance = Math.Max(4, GameSettings.RenderDistance - 8);
        return 0;
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

    private void CreateAuxBuffers()
    {
        if (_gridHeadTexture == 0) { _gridHeadTexture = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture3D, _gridHeadTexture); GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32i, OBJ_GRID_SIZE, OBJ_GRID_SIZE, OBJ_GRID_SIZE, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero); GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest); GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest); }
        if (_linkedListSsbo == 0) { _linkedListSsbo = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _linkedListSsbo); GL.BufferData(BufferTarget.ShaderStorageBuffer, 2 * 1024 * 1024 * 8, IntPtr.Zero, BufferUsageHint.DynamicDraw); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _linkedListSsbo); }
        if (_atomicCounterBuffer == 0) { _atomicCounterBuffer = GL.GenBuffer(); GL.BindBuffer(BufferTarget.AtomicCounterBuffer, _atomicCounterBuffer); GL.BufferData(BufferTarget.AtomicCounterBuffer, 4, IntPtr.Zero, BufferUsageHint.DynamicDraw); GL.BindBufferBase(BufferRangeTarget.AtomicCounterBuffer, 4, _atomicCounterBuffer); }
        if (_dynamicObjectsBuffer == 0) { _dynamicObjectsBuffer = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicObjectsBuffer); GL.BufferData(BufferTarget.ShaderStorageBuffer, 4096 * Marshal.SizeOf<GpuDynamicObject>(), IntPtr.Zero, BufferUsageHint.DynamicDraw); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicObjectsBuffer); }
        if (_beamFbo == 0) { _beamFbo = GL.GenFramebuffer(); }
        if (_editSsbo == 0) { _editSsbo = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _editSsbo); GL.BufferData(BufferTarget.ShaderStorageBuffer, 2048 * 16, IntPtr.Zero, BufferUsageHint.DynamicDraw); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, _editSsbo); }
    }

    private void UploadChunkVoxels(Chunk chunk, int globalSlot)
    {
        chunk.ReadDataUnsafe((srcVoxels, srcMasks) =>
        {
            if (srcVoxels == null) return;
            int bankIndex = globalSlot / _chunksPerBank;
            int localSlot = globalSlot % _chunksPerBank;
            if (bankIndex >= MAX_TOTAL_BANKS || _voxelSsboBanks[bankIndex] == 0) return;

            System.Buffer.BlockCopy(srcVoxels, 0, _chunkUploadBuffer, 0, ChunkVol);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _voxelSsboBanks[bankIndex]);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer,
                (IntPtr)((long)localSlot * PackedChunkSizeInInts * 4),
                PackedChunkSizeInInts * 4, _chunkUploadBuffer);

            if (srcMasks != null)
            {
                System.Buffer.BlockCopy(srcMasks, 0, _maskUploadBuffer, 0, ChunkMaskSizeInUlongs * 8);
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _maskSsbo);
                GL.BufferSubData(BufferTarget.ShaderStorageBuffer,
                    (IntPtr)((long)globalSlot * ChunkMaskSizeInUlongs * 8),
                    ChunkMaskSizeInUlongs * 8, _maskUploadBuffer);
            }
        });
    }

    private int GetPageTableIndex(Vector3i p)
        => (p.X & MASK_X) + PT_X * ((p.Y & MASK_Y) + PT_Y * (p.Z & MASK_Z));

    private void CleanupBuffers()
    {
        GL.UseProgram(0); GL.BindVertexArray(0); GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        for (int i = 0; i < 32; i++) GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, i, 0);
        GL.Flush(); GL.Finish();
        CleanupBanks();
        if (_maskSsbo != 0) { GL.DeleteBuffer(_maskSsbo); _maskSsbo = 0; }
        if (_pageTableTexture != 0) { GL.DeleteTexture(_pageTableTexture); _pageTableTexture = 0; }
        if (_gColorTexture != 0) { GL.DeleteTexture(_gColorTexture); _gColorTexture = 0; }
        if (_gDataTexture != 0) { GL.DeleteTexture(_gDataTexture); _gDataTexture = 0; }

        if (_shadowFbo != 0) { GL.DeleteFramebuffer(_shadowFbo); _shadowFbo = 0; }
        if (_shadowTexture != 0) { GL.DeleteTexture(_shadowTexture); _shadowTexture = 0; }
        if (_shadowPointLightTexture != 0) { GL.DeleteTexture(_shadowPointLightTexture); _shadowPointLightTexture = 0; }

        if (_shadowFullFbo != 0) { GL.DeleteFramebuffer(_shadowFullFbo); _shadowFullFbo = 0; }
        if (_shadowFullTexture != 0) { GL.DeleteTexture(_shadowFullTexture); _shadowFullTexture = 0; }
        if (_shadowPointLightFullTexture != 0) { GL.DeleteTexture(_shadowPointLightFullTexture); _shadowPointLightFullTexture = 0; }

        if (_compositeFbo != 0) { GL.DeleteFramebuffer(_compositeFbo); _compositeFbo = 0; }
        if (_mainColorTexture != 0) { GL.DeleteTexture(_mainColorTexture); _mainColorTexture = 0; }

        if (_debugFbo != 0) { GL.DeleteFramebuffer(_debugFbo); _debugFbo = 0; }
        if (_debugColorTexture != 0) { GL.DeleteTexture(_debugColorTexture); _debugColorTexture = 0; }

        _vctSystem?.Dispose();
        _vctSystem = null;
        if (_pointLightSsbo != 0) { GL.DeleteBuffer(_pointLightSsbo); _pointLightSsbo = 0; }
        CurrentAllocatedBytes = 0;
        System.Threading.Thread.Sleep(50);
    }

    private int LoadTexture(string path)
    {
        if (!File.Exists(path)) return 0;
        int handle = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, handle);
        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, image.Width, image.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, image.Data);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        return handle;
    }

    public void ReloadShader() => _shaderSystem.Compile(MAX_TOTAL_BANKS, _chunksPerBank);
    public void UploadAllVisibleChunks() { foreach (var c in _worldService.GetChunksSnapshot()) NotifyChunkLoaded(c); }
    public void ClearHoverVoxel() => SetHoverVoxel(new Vector3(-9999f), new Vector3(-9999f));

    public string GetMemoryDebugInfo()
    {
        int totalSlots = _activeBankCount * _chunksPerBank;
        int usedSlots = totalSlots - _freeSlots.Count;
        float percent = (float)usedSlots / totalSlots * 100f;
        return $"VRAM Banks: {_activeBankCount} | Slots: {usedSlots}/{totalSlots} ({percent:F1}%) | LoadedChunks: {_allocatedChunks.Count}";
    }

    public void Dispose()
    {
        CleanupBuffers();
        _quadVao = 0;
        _shaderSystem?.Dispose();
        _gridComputeShader?.Dispose();
        _clearGridShader?.Dispose();
        _svoManager?.Dispose();
    }
}