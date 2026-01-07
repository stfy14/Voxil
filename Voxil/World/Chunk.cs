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

    public Vector3i Position { get; }
    public WorldManager WorldManager { get; }

    private byte[] _voxels; // Nullable!
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
            for(int i = 0; i < Volume; i++) 
            {
                if (sourceArray[i] != MaterialType.Air) solidCounter++;
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
            }
            
            IsLoaded = true;
        }
        finally { _lock.ExitWriteLock(); }
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
            // Уведомления (координаты примерные, поправим при работе над физикой разрушения)
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