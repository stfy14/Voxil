using BepuPhysics;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Threading;

public class Chunk : IDisposable
{
    public const int ChunkSize = 16;
    public const int Volume = ChunkSize * ChunkSize * ChunkSize;
    
    public Vector3i Position { get; }
    public WorldManager WorldManager { get; }

    // ОПТИМИЗАЦИЯ: Используем byte вместо Enum/int. Экономия RAM в 4-8 раз.
    public byte[] Voxels = new byte[Volume];
    
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

    public void SetDataFromGenerator(Dictionary<Vector3i, MaterialType> voxelsDict)
    {
        // Этот метод старый и медленный, но если он используется, обновим его:
        if (_isDisposed) return;
        _lock.EnterWriteLock();
        try
        {
            Array.Fill(Voxels, (byte)0);
            SolidCount = 0;
            foreach (var kvp in voxelsDict)
            {
                // ... проверки границ ...
                int index = kvp.Key.X + ChunkSize * (kvp.Key.Y + ChunkSize * kvp.Key.Z);
                Voxels[index] = (byte)kvp.Value; // Каст к байту
                if (kvp.Value != MaterialType.Air) SolidCount++;
            }
            IsLoaded = true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // Метод для быстрого генератора (который мы делали в прошлый раз)
    public void SetDataFromArray(MaterialType[] sourceArray)
    {
        if (_isDisposed) return;
        _lock.EnterWriteLock();
        try
        {
            // Конвертируем и копируем
            // (В идеале генератор сразу должен работать с byte[], но пока конвертируем тут)
            for(int i=0; i < Volume; i++)
            {
                Voxels[i] = (byte)sourceArray[i];
            }
            
            // Пересчитываем SolidCount
            SolidCount = 0;
            for(int i=0; i<Volume; i++) if (Voxels[i] != 0) SolidCount++;
            
            IsLoaded = true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public byte[] GetVoxelsUnsafe() => Voxels;
    public ReaderWriterLockSlim GetLock() => _lock;

    public bool RemoveVoxelAndUpdate(Vector3i localPosition)
    {
        if (_isDisposed) return false;
        // ... (проверки границ) ...
        int index = localPosition.X + ChunkSize * (localPosition.Y + ChunkSize * localPosition.Z);
        bool removed = false;

        try
        {
            _lock.EnterWriteLock();
            try
            {
                if (Voxels[index] != 0) // 0 = Air
                {
                    Voxels[index] = 0;
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
            WorldManager.QueueDetachmentCheck(this, localPosition);
            WorldManager.RebuildPhysics(this);
            WorldManager.NotifyChunkModified(this); 
        }
        return removed;
    }

    public bool IsVoxelSolidAt(Vector3i localPos)
    {
        if (_isDisposed) return false;
        // ... (проверки границ) ...
        int index = localPos.X + ChunkSize * (localPos.Y + ChunkSize * localPos.Z);
        // Обратное преобразование byte -> MaterialType для проверки
        return MaterialRegistry.IsSolidForPhysics((MaterialType)Voxels[index]);
    }

    // ... (Остальные методы: OnPhysicsRebuilt, ClearPhysics, Dispose - без изменений) ...
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
        Voxels = null; 
    }
}