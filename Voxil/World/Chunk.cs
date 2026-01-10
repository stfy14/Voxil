using BepuPhysics;
using OpenTK.Mathematics;
using System;
using System.Buffers;
using System.Threading;

public class Chunk : IDisposable
{
    public const int ChunkSize = Constants.ChunkResolution; 
    public const int Volume = Constants.ChunkVolume;        
    public const int BlockSize = 4;
    private const int BlockShift = 2; 
    private const int BlockMaskBit = 3; 
    
    public const int BlocksPerAxis = ChunkSize / BlockSize; 
    public const int MasksCount = BlocksPerAxis * BlocksPerAxis * BlocksPerAxis; 

    public Vector3i Position { get; }
    public WorldManager WorldManager { get; }

    private byte[] _voxels;
    private ulong[] _blockMasks; 

    public int SolidCount { get; private set; } = 0;
    
    // НОВЫЕ ПОЛЯ ДЛЯ ОПТИМИЗАЦИИ
    private bool _isUniform = false;
    private MaterialType _uniformMaterial = MaterialType.Air;
    // ---------------------------

    public bool IsReadyForRendering { get; set; } = false;
    
    private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
    public bool IsLoaded { get; private set; } = false;
    private volatile bool _isDisposed = false;
    
    private StaticHandle _staticHandle;
    private bool _hasStaticBody = false;

    public Chunk(Vector3i position, WorldManager worldManager)
    {
        Position = position;
        WorldManager = worldManager;
        IsReadyForRendering = false;
    }

    // === ОПТИМИЗИРОВАННЫЙ МЕТОД ===
    // Теперь это просто геттер, который стоит 0 процессорного времени.
    public bool IsFullyUniform(out MaterialType mat)
    {
        // 1. Быстрый возврат предрасчитанного значения
        if (_isUniform)
        {
            mat = _uniformMaterial;
            return true;
        }

        // 2. Если чанк пустой (воздух) - считаем его uniform Air
        if (SolidCount == 0)
        {
            mat = MaterialType.Air;
            return true;
        }
        
        mat = MaterialType.Air;
        return false;
    }

    public void SetDataFromArray(MaterialType[] sourceArray)
    {
        if (_isDisposed) return;
        _lock.EnterWriteLock();
        try
        {
            int solidCounter = 0;
            if (_blockMasks == null) _blockMasks = ArrayPool<ulong>.Shared.Rent(MasksCount);
            Array.Clear(_blockMasks, 0, MasksCount);

            int cShift = Constants.BitShift; int cMask = Constants.BitMask;   
            int bShift = BlockShift; int bMask = BlockMaskBit; int bPerAxis = BlocksPerAxis;

            // --- ЛОГИКА ОПРЕДЕЛЕНИЯ UNIFORM ---
            bool potentiallyUniform = true;
            MaterialType firstMat = sourceArray[0];
            // ----------------------------------

            for(int i = 0; i < Volume; i++) 
            {
                MaterialType currentMat = sourceArray[i];

                // Проверка на однородность "на лету"
                if (potentiallyUniform && currentMat != firstMat)
                {
                    potentiallyUniform = false;
                }

                if (currentMat != MaterialType.Air) 
                {
                    solidCounter++;
                    int z = i >> (cShift * 2);
                    int y = (i >> cShift) & cMask;
                    int x = i & cMask;
                    int bx = x >> bShift; int by = y >> bShift; int bz = z >> bShift;
                    int blockIndex = bx + bPerAxis * (by + bPerAxis * bz);
                    int lx = x & bMask; int ly = y & bMask; int lz = z & bMask;
                    int localBitIndex = lx | (ly << 2) | (lz << 4);
                    _blockMasks[blockIndex] |= (1UL << localBitIndex);
                }
            }
            SolidCount = solidCounter;

            // Финализируем данные об однородности
            if (potentiallyUniform)
            {
                _isUniform = true;
                _uniformMaterial = firstMat;
            }
            else
            {
                _isUniform = false;
                _uniformMaterial = MaterialType.Air;
            }

            // Аллокация вокселей
            if (SolidCount > 0)
            {
                if (_voxels == null) _voxels = ArrayPool<byte>.Shared.Rent(Volume);
                
                // ОПТИМИЗАЦИЯ КОПИРОВАНИЯ
                // Вместо цикла for используем Buffer.BlockCopy или Span, это быстрее
                // Но так как sourceArray - MaterialType[] (enum/int?), а _voxels - byte[],
                // нужно приведение. Если MaterialType это byte - можно BlockCopy.
                // Если нет - оставляем цикл.
                for(int i=0; i < Volume; i++) _voxels[i] = (byte)sourceArray[i];
            }
            else
            {
                if (_voxels != null) { ArrayPool<byte>.Shared.Return(_voxels); _voxels = null; }
            }
            IsLoaded = true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool IsVoxelSolidAt(Vector3i localPos)
    {
        if (_isDisposed || _voxels == null) return false;
        if (localPos.X < 0 || localPos.X >= ChunkSize || localPos.Y < 0 || localPos.Y >= ChunkSize || localPos.Z < 0 || localPos.Z >= ChunkSize) return false;
        _lock.EnterReadLock();
        try { int index = localPos.X + ChunkSize * (localPos.Y + ChunkSize * localPos.Z); return MaterialRegistry.IsSolidForPhysics((MaterialType)_voxels[index]); }
        finally { _lock.ExitReadLock(); }
    }

    public bool RemoveVoxelAndUpdate(Vector3i localPosition)
    {
        if (_isDisposed || _voxels == null) return false;
        int index = localPosition.X + ChunkSize * (localPosition.Y + ChunkSize * localPosition.Z);
        bool removed = false;
        try {
            _lock.EnterWriteLock();
            try {
                if (_voxels[index] != 0) {
                    _voxels[index] = 0; SolidCount--;
                    
                    // --- ОБНОВЛЕНИЕ UNIFORM ---
                    // Если мы удалили блок из чанка, который был uniform solid,
                    // он перестает быть uniform (становится Material + Air).
                    // Если он был uniform Air, мы бы сюда не зашли (voxels=null).
                    if (_isUniform)
                    {
                        _isUniform = false;
                        // _uniformMaterial можно не сбрасывать, флаг false главнее
                    }
                    // --------------------------

                    int x=localPosition.X; int y=localPosition.Y; int z=localPosition.Z;
                    int bx=x>>BlockShift; int by=y>>BlockShift; int bz=z>>BlockShift;
                    int blockIndex = bx + BlocksPerAxis * (by + BlocksPerAxis * bz);
                    int lx=x&BlockMaskBit; int ly=y&BlockMaskBit; int lz=z&BlockMaskBit;
                    int localBitIndex = lx | (ly<<2) | (lz<<4);
                    _blockMasks[blockIndex] &= ~(1UL << localBitIndex);
                    removed = true;
                    if (SolidCount == 0) { ArrayPool<byte>.Shared.Return(_voxels); _voxels = null; }
                }
            } finally { _lock.ExitWriteLock(); }
        } catch (ObjectDisposedException) { return false; }

        if (removed && IsLoaded) {
            WorldManager.NotifyVoxelFastDestroyed(this.Position * Constants.ChunkSizeWorld + localPosition);
            WorldManager.RebuildPhysics(this);
            WorldManager.NotifyChunkModified(this); 
        }
        return removed;
    }
    
    // ... Остальные методы (ReadMasksUnsafe, Dispose и т.д.) без изменений ...
    public void ReadMasksUnsafe(Action<ulong[]> action) { if (_isDisposed) return; _lock.EnterReadLock(); try { action(_blockMasks); } finally { _lock.ExitReadLock(); } }
    public void ReadDataUnsafe(Action<byte[], ulong[]> action) { if (_isDisposed) return; _lock.EnterReadLock(); try { action(_voxels, _blockMasks); } finally { _lock.ExitReadLock(); } }
    public byte[] GetVoxelsCopy() { if (_isDisposed) return null; _lock.EnterReadLock(); try { if (_voxels == null) return null; var copy = ArrayPool<byte>.Shared.Rent(Volume); Buffer.BlockCopy(_voxels, 0, copy, 0, Volume); return copy; } finally { _lock.ExitReadLock(); } }
    public void OnPhysicsRebuilt(StaticHandle handle) { if (_isDisposed) return; ClearPhysics(); _staticHandle = handle; _hasStaticBody = true; WorldManager.RegisterChunkStatic(handle, this); }
    private void ClearPhysics() { if (_hasStaticBody) { WorldManager.PhysicsWorld.RemoveStaticBody(_staticHandle); WorldManager.UnregisterChunkStatic(_staticHandle); _hasStaticBody = false; } }
    public void Dispose() { if (_isDisposed) return; _lock.EnterWriteLock(); try { if (_isDisposed) return; _isDisposed = true; IsLoaded = false; ClearPhysics(); if (_voxels != null) { ArrayPool<byte>.Shared.Return(_voxels); _voxels = null; } if (_blockMasks != null) { ArrayPool<ulong>.Shared.Return(_blockMasks); _blockMasks = null; } } finally { _lock.ExitWriteLock(); _lock.Dispose(); } }
}