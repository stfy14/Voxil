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
    private Stopwatch _stopwatch = new Stopwatch(); 

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
        
        // 1. Получаем копию байтов (арендованную)
        var voxels = chunk.GetVoxelsCopy();
        if (voxels == null) return; 

        // 2. Арендуем массив для конвертации в MaterialType
        var matArray = System.Buffers.ArrayPool<MaterialType>.Shared.Rent(Constants.ChunkVolume);
        
        try
        {
            // Быстрая конвертация byte -> MaterialType
            for(int i = 0; i < Constants.ChunkVolume; i++) 
            {
                matArray[i] = (MaterialType)voxels[i];
            }
            
            // Возвращаем байты сразу, они больше не нужны
            System.Buffers.ArrayPool<byte>.Shared.Return(voxels); 
            voxels = null;

            // --- ЗАМЕР ВРЕМЕНИ ---
            long start = Stopwatch.GetTimestamp();
            
            // Передаем арендованный массив
            var data = VoxelPhysicsBuilder.GenerateColliders(matArray, chunk.Position);
            
            long end = Stopwatch.GetTimestamp();
            if (PerformanceMonitor.IsEnabled) 
            {
                PerformanceMonitor.Record(ThreadType.ChunkPhys, end - start);
            }
            // ---------------------

            _outputQueue.Enqueue(new PhysicsBuildResult(chunk, data));
        }
        finally
        {
            // Обязательно возвращаем массивы, даже при ошибке
            if (voxels != null) System.Buffers.ArrayPool<byte>.Shared.Return(voxels);
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