using BepuPhysics;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Buffers;

public class Chunk : IDisposable
{
    public const int ChunkSize = 16;
    public const int Volume = ChunkSize * ChunkSize * ChunkSize;
    
    public Vector3i Position { get; }
    public WorldManager WorldManager { get; }

    // --- ИНКАПСУЛЯЦИЯ: Приватное поле ---
    private byte[] _voxels; 
    
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
        
        // Аренда массива
        _voxels = ArrayPool<byte>.Shared.Rent(Volume);
        Array.Clear(_voxels, 0, Volume);
    }

    public void SetDataFromArray(MaterialType[] sourceArray)
    {
        if (_isDisposed) return;
        
        _lock.EnterWriteLock();
        try
        {
            // Используем _voxels
            Buffer.BlockCopy(sourceArray, 0, _voxels, 0, Volume);
            
            SolidCount = 0;
            for(int i=0; i < Volume; i++) 
            {
                if (_voxels[i] != 0) SolidCount++;
            }
            
            IsLoaded = true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // Временный метод для совместимости (если где-то нужен сырой массив, но лучше избегать)
    // Используй с осторожностью!
    public byte[] GetVoxelsUnsafe() => _voxels;
    
    // Выполняет действие с сырым массивом вокселей под блокировкой чтения.
    // Это безопасно и быстро (без лишних аллокаций).
    public void ReadVoxelsUnsafe(Action<byte[]> action)
    {
        if (_isDisposed) return;
        
        _lock.EnterReadLock();
        try
        {
            action(_voxels);
        }
        finally { _lock.ExitReadLock(); }
    }

    // --- НОВЫЙ МЕТОД: Безопасная копия для физики ---
    // PhysicsBuilder теперь должен использовать этот метод, а не лезть в массив напрямую
    public byte[] GetVoxelsCopy()
    {
        if (_isDisposed) return null;
        _lock.EnterReadLock();
        try
        {
            var copy = ArrayPool<byte>.Shared.Rent(Volume);
            Buffer.BlockCopy(_voxels, 0, copy, 0, Volume);
            return copy;
        }
        finally { _lock.ExitReadLock(); }
    }

    public ReaderWriterLockSlim GetLock() => _lock;

    public bool RemoveVoxelAndUpdate(Vector3i localPosition)
    {
        if (_isDisposed) return false;
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
                    removed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }
        }
        catch (ObjectDisposedException) { return false; }

        if (removed && IsLoaded)
        {
            WorldManager.NotifyVoxelFastDestroyed(this.Position * ChunkSize + localPosition);
            WorldManager.RebuildPhysics(this);
            WorldManager.NotifyChunkModified(this); 
        }
        return removed;
    }

    public MaterialType GetMaterial(int x, int y, int z)
    {
        if (_isDisposed) return MaterialType.Air;
        if (x < 0 || x >= ChunkSize || y < 0 || y >= ChunkSize || z < 0 || z >= ChunkSize) return MaterialType.Air;

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
        if (_isDisposed) return false;
        int index = localPos.X + ChunkSize * (localPos.Y + ChunkSize * localPos.Z);
        
        _lock.EnterReadLock();
        try 
        {
            return MaterialRegistry.IsSolidForPhysics((MaterialType)_voxels[index]);
        }
        finally { _lock.ExitReadLock(); }
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
        _isDisposed = true;
        IsLoaded = false;
        ClearPhysics();
        _lock.Dispose();
        
        if (_voxels != null)
        {
            ArrayPool<byte>.Shared.Return(_voxels);
            _voxels = null;
        }
    }
}