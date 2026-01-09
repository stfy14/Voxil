using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Diagnostics;

public class AsyncChunkPhysics : IDisposable
{
    private readonly BlockingCollection<PhysicsBuildTask> _inputQueue = new(new ConcurrentQueue<PhysicsBuildTask>());
    private readonly ConcurrentQueue<PhysicsBuildTask> _urgentQueue = new();
    private readonly ConcurrentQueue<PhysicsBuildResult> _outputQueue = new();

    private readonly Thread _workerThread;
    private volatile bool _isDisposed;

    public AsyncChunkPhysics()
    {
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true, Name = "PhysicsBuilder", Priority = ThreadPriority.BelowNormal
        };
        _workerThread.Start();
    }

    public void EnqueueTask(Chunk chunk, bool urgent = false)
    {
        if (_isDisposed || chunk == null) return;
        var task = new PhysicsBuildTask(chunk);
        if (urgent) _urgentQueue.Enqueue(task);
        else _inputQueue.Add(task);
    }

    public bool TryGetResult(out PhysicsBuildResult result) => _outputQueue.TryDequeue(out result);

    // Метод для полной очистки при перезагрузке
    public void Clear()
    {
        // 1. Чистим срочную очередь (ConcurrentQueue - TryDequeue)
        while (_urgentQueue.TryDequeue(out _)) { }

        // 2. Чистим входную очередь (BlockingCollection - TryTake)
        while (_inputQueue.TryTake(out _)) { }

        // 3. Чистим выходную очередь (ConcurrentQueue - TryDequeue)
        while (_outputQueue.TryDequeue(out var result)) 
        {
            // Если нужно освободить ресурсы результата (VoxelCollider[]), делаем это
            result.Data.Dispose();
        }
    }

    private void WorkerLoop()
    {
        while (!_isDisposed)
        {
            try
            {
                PhysicsBuildTask task = default;
                bool gotTask = false;
                if (_urgentQueue.TryDequeue(out task)) gotTask = true;
                else if (_inputQueue.TryTake(out task, 50)) gotTask = true;

                if (gotTask && task.IsValid) ProcessChunk(task.ChunkToProcess);
            }
            catch (Exception ex) { Console.WriteLine($"[PhysBuilder] Error: {ex.Message}"); }
        }
    }

    private void ProcessChunk(Chunk chunk)
    {
        if (chunk == null || !chunk.IsLoaded) return;
        
        var voxels = chunk.GetVoxelsCopy();
        if (voxels == null) return; 

        var matArray = System.Buffers.ArrayPool<MaterialType>.Shared.Rent(Constants.ChunkVolume);
        try
        {
            for(int i = 0; i < Constants.ChunkVolume; i++) matArray[i] = (MaterialType)voxels[i];
            System.Buffers.ArrayPool<byte>.Shared.Return(voxels); 
            voxels = null;

            long start = Stopwatch.GetTimestamp();
            var data = VoxelPhysicsBuilder.GenerateColliders(matArray, chunk.Position);
            long end = Stopwatch.GetTimestamp();

            if (PerformanceMonitor.IsEnabled) 
                PerformanceMonitor.Record(ThreadType.ChunkPhys, end - start);

            _outputQueue.Enqueue(new PhysicsBuildResult(chunk, data));
        }
        finally
        {
            if(voxels != null) System.Buffers.ArrayPool<byte>.Shared.Return(voxels);
            System.Buffers.ArrayPool<MaterialType>.Shared.Return(matArray);
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        _inputQueue.CompleteAdding();
        if (_workerThread.IsAlive) _workerThread.Join(100);
        _inputQueue.Dispose();
    }
}