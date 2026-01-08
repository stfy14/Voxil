using BepuPhysics;
using OpenTK.Mathematics;
using System;
using System.Buffers;
using System.Threading;

public class Chunk : IDisposable
{
    // Ссылки на константы для совместимости
    public const int ChunkSize = Constants.ChunkResolution; // 64
    public const int Volume = Constants.ChunkVolume;        // 262144

    // --- НОВЫЕ КОНСТАНТЫ (Bitwise Masking) ---
    public const int BlockSize = 4;
    public const int BlocksPerAxis = ChunkSize / BlockSize; // 16
    public const int MasksCount = BlocksPerAxis * BlocksPerAxis * BlocksPerAxis; // 4096

    public Vector3i Position { get; }
    public WorldManager WorldManager { get; }

    private byte[] _voxels; // Nullable!
    private ulong[] _blockMasks; // Маски для оптимизации (Bitwise)

    public int SolidCount { get; private set; } = 0;
    
    private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
    public bool IsLoaded { get; private set; } = false;
    private volatile bool _isDisposed = false;
    
    private StaticHandle _staticHandle;
    private bool _hasStaticBody = false;

    public Chunk(Vector3i position, WorldManager worldManager)
    {
        Position = position;
        WorldManager = worldManager;
    }

    public void SetDataFromArray(MaterialType[] sourceArray)
    {
        if (_isDisposed) return;
        
        _lock.EnterWriteLock();
        try
        {
            int solidCounter = 0;
            
            // Инициализация или очистка масок
            if (_blockMasks == null) _blockMasks = ArrayPool<ulong>.Shared.Rent(MasksCount);
            Array.Clear(_blockMasks, 0, MasksCount);

            bool hasSolids = false;

            for(int i = 0; i < Volume; i++) 
            {
                if (sourceArray[i] != MaterialType.Air) 
                {
                    solidCounter++;
                    hasSolids = true;

                    // --- ГЕНЕРАЦИЯ МАСКИ ---
                    // Индекс i = x + 64 * (y + 64 * z)
                    int z = i / (ChunkSize * ChunkSize);
                    int rem = i % (ChunkSize * ChunkSize);
                    int y = rem / ChunkSize;
                    int x = rem % ChunkSize;

                    // Координаты макро-блока
                    int bx = x / BlockSize;
                    int by = y / BlockSize;
                    int bz = z / BlockSize;
                    int blockIndex = bx + BlocksPerAxis * (by + BlocksPerAxis * bz);

                    // Координаты внутри блока (бит)
                    int lx = x % BlockSize;
                    int ly = y % BlockSize;
                    int lz = z % BlockSize;
                    int localBitIndex = lx + BlockSize * (ly + BlockSize * lz); // 0..63

                    // Устанавливаем бит
                    _blockMasks[blockIndex] |= (1UL << localBitIndex);
                }
            }
            SolidCount = solidCounter;

            if (SolidCount > 0)
            {
                if (_voxels == null) _voxels = ArrayPool<byte>.Shared.Rent(Volume);
                // Безопасное копирование
                for(int i=0; i < Volume; i++) _voxels[i] = (byte)sourceArray[i];
            }
            else
            {
                if (_voxels != null)
                {
                    ArrayPool<byte>.Shared.Return(_voxels);
                    _voxels = null;
                }
                // Если чанк пустой, маски можно не очищать (они уже очищены или не будут читаться),
                // но массив _blockMasks мы пока держим.
            }
            
            IsLoaded = true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // --- Метод для чтения масок (НОВЫЙ) ---
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

    // --- ВОССТАНОВЛЕННЫЙ МЕТОД (Используется в физике) ---
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

                    // --- ОБНОВЛЕНИЕ МАСКИ ПРИ УДАЛЕНИИ ---
                    int bx = localPosition.X / BlockSize;
                    int by = localPosition.Y / BlockSize;
                    int bz = localPosition.Z / BlockSize;
                    int blockIndex = bx + BlocksPerAxis * (by + BlocksPerAxis * bz);
                    
                    int lx = localPosition.X % BlockSize;
                    int ly = localPosition.Y % BlockSize;
                    int lz = localPosition.Z % BlockSize;
                    int localBitIndex = lx + BlockSize * (ly + BlockSize * lz);
                    
                    // Сбрасываем бит в 0. Если блок станет пустым (0), шейдер будет его пропускать.
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
            // Уведомления
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
        
        // Блокируем запись, чтобы никто не начал читать/писать пока мы удаляем
        _lock.EnterWriteLock(); 
        try
        {
            if (_isDisposed) return; // Двойная проверка
            _isDisposed = true;
            IsLoaded = false;
            
            // Очищаем физику (это безопасно делать под локом, т.к. RegisterChunkStatic берет свой лок)
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
            _lock.Dispose(); // Dispose самого лока
        }
    }
}