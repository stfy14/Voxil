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
        
        // Получаем массив вокселей (потокобезопасно)
        var voxels = chunk.GetVoxelsCopy();
        if (voxels == null) return; 

        // Генерируем коллайдеры с помощью Greedy Meshing
        MaterialType[] matArray = new MaterialType[Constants.ChunkVolume];
        for(int i=0; i<Constants.ChunkVolume; i++) matArray[i] = (MaterialType)voxels[i];
        System.Buffers.ArrayPool<byte>.Shared.Return(voxels); 

        // --- ЗАМЕР ВРЕМЕНИ ---
        long start = Stopwatch.GetTimestamp();
        
        var data = VoxelPhysicsBuilder.GenerateColliders(matArray, chunk.Position);
        
        long end = Stopwatch.GetTimestamp();
        if (PerformanceMonitor.IsEnabled) 
        {
            // ThreadType.ChunkPhys - это то, что отображается как "Build (Phys)"
            PerformanceMonitor.Record(ThreadType.ChunkPhys, end - start);
        }
        // ---------------------

        _outputQueue.Enqueue(new PhysicsBuildResult(chunk, data));
    }

    public void Dispose()
    {
        _isDisposed = true;
        _inputQueue.CompleteAdding();
        if (_workerThread.IsAlive) _workerThread.Join(100);
        _inputQueue.Dispose();
    }
}