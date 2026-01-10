// --- START OF FILE Chunk.cs ---

using BepuPhysics;
using OpenTK.Mathematics;
using System;
using System.Buffers;
using System.Threading;

public class Chunk : IDisposable
{
    // Ссылки на константы для совместимости
    public const int ChunkSize = Constants.ChunkResolution; 
    public const int Volume = Constants.ChunkVolume;        

    // --- БИТОВЫЕ КОНСТАНТЫ ---
    public const int BlockSize = 4;
    private const int BlockShift = 2; // 2^2 = 4
    private const int BlockMaskBit = 3; // 00000011 (3)
    
    public const int BlocksPerAxis = ChunkSize / BlockSize; 
    public const int MasksCount = BlocksPerAxis * BlocksPerAxis * BlocksPerAxis; 

    public Vector3i Position { get; }
    public WorldManager WorldManager { get; }

    private byte[] _voxels; // Nullable!
    private ulong[] _blockMasks; 

    public int SolidCount { get; private set; } = 0;
    
    // Флаг готовности для рендера (из предыдущих шагов)
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

    public void SetDataFromArray(MaterialType[] sourceArray)
    {
        if (_isDisposed) return;
        
        _lock.EnterWriteLock();
        try
        {
            int solidCounter = 0;
            
            if (_blockMasks == null) _blockMasks = ArrayPool<ulong>.Shared.Rent(MasksCount);
            Array.Clear(_blockMasks, 0, MasksCount);

            bool hasSolids = false;

            // Кэшируем константы для скорости (JIT это любит)
            int cShift = Constants.BitShift; 
            int cMask = Constants.BitMask;   
            int bShift = BlockShift;
            int bMask = BlockMaskBit;
            int bPerAxis = BlocksPerAxis;
            // Сдвиги для макро-индекса блока
            int bLayerStride = bPerAxis * bPerAxis;

            for(int i = 0; i < Volume; i++) 
            {
                if (sourceArray[i] != MaterialType.Air) 
                {
                    solidCounter++;
                    hasSolids = true;

                    // --- ОПТИМИЗАЦИЯ: БИТОВЫЕ ОПЕРАЦИИ ВМЕСТО ДЕЛЕНИЯ ---
                    // Индекс i = x + Size * (y + Size * z)
                    // z = i / (Size^2) -> i >> (shift * 2)
                    int z = i >> (cShift * 2);
                    
                    // rem = i % (Size^2). В битах это отсечение старших битов Z.
                    // (1 << (cShift * 2)) - 1 создает маску (например, 1023 для 32x32)
                    // Но проще: y = (i >> cShift) & cMask
                    int y = (i >> cShift) & cMask;
                    
                    // x = i % Size -> i & cMask
                    int x = i & cMask;

                    // Координаты макро-блока (деление на 4 -> сдвиг на 2)
                    int bx = x >> bShift;
                    int by = y >> bShift;
                    int bz = z >> bShift;
                    
                    // Индекс блока в массиве масок
                    int blockIndex = bx + bPerAxis * (by + bPerAxis * bz);

                    // Координаты внутри блока (x % 4 -> x & 3)
                    int lx = x & bMask;
                    int ly = y & bMask;
                    int lz = z & bMask;
                    
                    // Бит внутри ulong (0..63)
                    // bit = lx + 4 * ly + 16 * lz
                    // bit = lx + (ly << 2) + (lz << 4)
                    int localBitIndex = lx | (ly << 2) | (lz << 4);

                    // Устанавливаем бит
                    _blockMasks[blockIndex] |= (1UL << localBitIndex);
                }
            }
            SolidCount = solidCounter;

            if (SolidCount > 0)
            {
                if (_voxels == null) _voxels = ArrayPool<byte>.Shared.Rent(Volume);
                for(int i=0; i < Volume; i++) _voxels[i] = (byte)sourceArray[i];
            }
            else
            {
                if (_voxels != null)
                {
                    ArrayPool<byte>.Shared.Return(_voxels);
                    _voxels = null;
                }
            }
            
            IsLoaded = true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void ReadMasksUnsafe(Action<ulong[]> action)
    {
        if (_isDisposed) return;
        _lock.EnterReadLock();
        try { action(_blockMasks); } finally { _lock.ExitReadLock(); }
    }
    
    public void ReadDataUnsafe(Action<byte[], ulong[]> action)
    {
        if (_isDisposed) return;
        _lock.EnterReadLock();
        try 
        { 
            action(_voxels, _blockMasks); 
        } 
        finally 
        { 
            _lock.ExitReadLock(); 
        }
    }

    public byte[] GetVoxelsUnsafe() => _voxels;
    
    public void ReadVoxelsUnsafe(Action<byte[]> action)
    {
        if (_isDisposed) return;
        _lock.EnterReadLock();
        try { action(_voxels); } finally { _lock.ExitReadLock(); }
    }

    public byte[] GetVoxelsCopy()
    {
        if (_isDisposed) return null;
        _lock.EnterReadLock();
        try
        {
            if (_voxels == null) return null;
            var copy = ArrayPool<byte>.Shared.Rent(Volume);
            Buffer.BlockCopy(_voxels, 0, copy, 0, Volume);
            return copy;
        }
        finally { _lock.ExitReadLock(); }
    }

    public MaterialType GetMaterial(int x, int y, int z)
    {
        if (_isDisposed || _voxels == null) return MaterialType.Air;
        if ((uint)x >= ChunkSize || (uint)y >= ChunkSize || (uint)z >= ChunkSize) return MaterialType.Air;

        _lock.EnterReadLock();
        try
        {
            int index = x + ChunkSize * (y + ChunkSize * z);
            return (MaterialType)_voxels[index];
        }
        finally { _lock.ExitReadLock(); }
    }

    public bool IsVoxelSolidAt(Vector3i localPos)
    {
        if (_isDisposed || _voxels == null) return false;
        if (localPos.X < 0 || localPos.X >= ChunkSize ||
            localPos.Y < 0 || localPos.Y >= ChunkSize ||
            localPos.Z < 0 || localPos.Z >= ChunkSize) return false;

        _lock.EnterReadLock();
        try 
        {
            int index = localPos.X + ChunkSize * (localPos.Y + ChunkSize * localPos.Z);
            return MaterialRegistry.IsSolidForPhysics((MaterialType)_voxels[index]);
        }
        finally { _lock.ExitReadLock(); }
    }

    public bool RemoveVoxelAndUpdate(Vector3i localPosition)
    {
        if (_isDisposed || _voxels == null) return false;
        
        if (localPosition.X < 0 || localPosition.X >= ChunkSize ||
            localPosition.Y < 0 || localPosition.Y >= ChunkSize ||
            localPosition.Z < 0 || localPosition.Z >= ChunkSize) return false;

        int index = localPosition.X + ChunkSize * (localPosition.Y + ChunkSize * localPosition.Z);
        bool removed = false;

        try
        {
            _lock.EnterWriteLock();
            try
            {
                if (_voxels[index] != 0)
                {
                    _voxels[index] = 0;
                    SolidCount--;

                    // --- ОПТИМИЗИРОВАННОЕ ОБНОВЛЕНИЕ МАСКИ ---
                    int x = localPosition.X;
                    int y = localPosition.Y;
                    int z = localPosition.Z;

                    int bx = x >> BlockShift;
                    int by = y >> BlockShift;
                    int bz = z >> BlockShift;
                    int blockIndex = bx + BlocksPerAxis * (by + BlocksPerAxis * bz);
                    
                    int lx = x & BlockMaskBit;
                    int ly = y & BlockMaskBit;
                    int lz = z & BlockMaskBit;
                    int localBitIndex = lx | (ly << 2) | (lz << 4);
                    
                    _blockMasks[blockIndex] &= ~(1UL << localBitIndex);
                    // -------------------------------------

                    removed = true;
                    if (SolidCount == 0)
                    {
                        ArrayPool<byte>.Shared.Return(_voxels);
                        _voxels = null;
                    }
                }
            }
            finally { _lock.ExitWriteLock(); }
        }
        catch (ObjectDisposedException) { return false; }

        if (removed && IsLoaded)
        {
            WorldManager.NotifyVoxelFastDestroyed(this.Position * Constants.ChunkSizeWorld + localPosition);
            WorldManager.RebuildPhysics(this);
            WorldManager.NotifyChunkModified(this); 
        }
        return removed;
    }

    public void OnPhysicsRebuilt(StaticHandle handle)
    {
        if (_isDisposed) return;
        ClearPhysics();
        _staticHandle = handle;
        _hasStaticBody = true;
        WorldManager.RegisterChunkStatic(handle, this);
    }

    private void ClearPhysics()
    {
        if (_hasStaticBody)
        {
            WorldManager.PhysicsWorld.RemoveStaticBody(_staticHandle);
            WorldManager.UnregisterChunkStatic(_staticHandle);
            _hasStaticBody = false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _lock.EnterWriteLock(); 
        try
        {
            if (_isDisposed) return;
            _isDisposed = true;
            IsLoaded = false;
            ClearPhysics();

            if (_voxels != null)
            {
                ArrayPool<byte>.Shared.Return(_voxels);
                _voxels = null;
            }
            if (_blockMasks != null)
            {
                ArrayPool<ulong>.Shared.Return(_blockMasks);
                _blockMasks = null;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
            _lock.Dispose();
        }
    }
}