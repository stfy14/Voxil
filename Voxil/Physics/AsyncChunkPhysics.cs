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

    // Реюзер буфера для Greedy Meshing (чтобы не выделять память каждый раз)
    private bool[] _reusedVisitedBuffer; 

    public AsyncChunkPhysics()
    {
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true, 
            Name = "PhysicsBuilder", 
            Priority = ThreadPriority.AboveNormal // <--- БЫЛО BelowNormal
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

    public void Clear()
    {
        while (_urgentQueue.TryDequeue(out _)) { }
        while (_inputQueue.TryTake(out _)) { }
        while (_outputQueue.TryDequeue(out var result)) result.Data.Dispose();
    }

    private void WorkerLoop()
    {
        // Выделяем буфер ОДИН РАЗ на весь жизненный цикл потока
        _reusedVisitedBuffer = new bool[Constants.ChunkVolume];

        while (!_isDisposed)
        {
            try
            {
                PhysicsBuildTask task = default;
                bool gotTask = false;
                if (_urgentQueue.TryDequeue(out task)) gotTask = true;
                else if (_inputQueue.TryTake(out task, 50)) gotTask = true;

                if (gotTask && task.IsValid) 
                {
                    ProcessChunk(task.ChunkToProcess);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[PhysBuilder] Error: {ex.Message}"); }
        }
    }

    private void ProcessChunk(Chunk chunk)
    {
        if (chunk == null || !chunk.IsLoaded) return;
        
        // Получаем копию вокселей (потокобезопасно)
        var voxels = chunk.GetVoxelsCopy();
        if (voxels == null) return; // Чанк пустой

        // Преобразуем byte[] в MaterialType[] (для совместимости с методом Builder'а)
        // В идеале Builder переписать на byte[], но пока сделаем через Pool
        var matArray = System.Buffers.ArrayPool<MaterialType>.Shared.Rent(Constants.ChunkVolume);
        
        try
        {
            // Быстрое копирование
            for(int i = 0; i < Constants.ChunkVolume; i++) matArray[i] = (MaterialType)voxels[i];
            
            // Возвращаем байтовый массив сразу
            System.Buffers.ArrayPool<byte>.Shared.Return(voxels); 
            voxels = null;

            long start = Stopwatch.GetTimestamp();
            
            // ВАЖНО: Передаем наш переиспользуемый буфер
            var data = VoxelPhysicsBuilder.GenerateColliders(matArray, chunk.Position, _reusedVisitedBuffer);
            
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
        _reusedVisitedBuffer = null;
    }
}