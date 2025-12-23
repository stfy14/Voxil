using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Threading;

public class AsyncChunkPhysics : IDisposable
{
    // Очереди
    private readonly BlockingCollection<PhysicsBuildTask> _inputQueue = new(new ConcurrentQueue<PhysicsBuildTask>());
    private readonly ConcurrentQueue<PhysicsBuildTask> _urgentQueue = new();
    private readonly ConcurrentQueue<PhysicsBuildResult> _outputQueue = new();

    private readonly Thread _workerThread;
    private volatile bool _isDisposed;

    public AsyncChunkPhysics()
    {
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "PhysicsBuilder",
            Priority = ThreadPriority.BelowNormal
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

    public bool TryGetResult(out PhysicsBuildResult result)
    {
        return _outputQueue.TryDequeue(out result);
    }

    private void WorkerLoop()
    {
        while (!_isDisposed)
        {
            try
            {
                PhysicsBuildTask task = default;
                bool gotTask = false;

                // Сначала проверяем срочную очередь
                if (_urgentQueue.TryDequeue(out task)) gotTask = true;
                // Потом обычную (с ожиданием)
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

        // Копируем воксели в безопасной среде
        // Используем ArrayPool для временного буфера сырых данных
        var rawVoxels = System.Buffers.ArrayPool<MaterialType>.Shared.Rent(Chunk.Volume);
        
        // Используем наш новый метод в Chunk.cs, который мы сделали ранее,
        // ЛИБО (для скорости) копируем напрямую под локом, как было раньше.
        // Давай сделаем через GetVoxelsCopy(), который мы добавили, но он возвращает новый массив.
        // Чтобы не аллоцировать лишнее, скопируем вручную тут:
        
        chunk.ReadVoxelsUnsafe((srcBytes) => 
        {
             // Копируем byte[] в MaterialType[] (каст байт в байт)
             Buffer.BlockCopy(srcBytes, 0, rawVoxels, 0, Chunk.Volume);
        });

        // Строим коллайдеры
        var resultData = VoxelPhysicsBuilder.GenerateColliders(rawVoxels, chunk.Position);
        
        // Возвращаем буфер в пул
        System.Buffers.ArrayPool<MaterialType>.Shared.Return(rawVoxels);

        _outputQueue.Enqueue(new PhysicsBuildResult(chunk, resultData));
    }

    public void Dispose()
    {
        _isDisposed = true;
        _inputQueue.CompleteAdding();
        if (_workerThread.IsAlive) _workerThread.Join(100);
        _inputQueue.Dispose();
    }
}