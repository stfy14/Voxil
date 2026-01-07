using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

public class AsyncChunkGenerator : IDisposable
{
    private readonly IWorldGenerator _generator;
    private readonly BlockingCollection<ChunkGenerationTask> _inputQueue = new(new ConcurrentQueue<ChunkGenerationTask>());
    // Лимит 64 чанка на выходе
    private readonly BlockingCollection<ChunkGenerationResult> _outputQueue = new(new ConcurrentQueue<ChunkGenerationResult>(), 64);
    
    private readonly List<Thread> _threads = new();
    private CancellationTokenSource _threadsCts = new();
    private volatile bool _isDisposed;

    public AsyncChunkGenerator(int seed, int threadCount)
    {
        _generator = new PerlinGenerator(seed);
        SetThreadCount(threadCount);
    }

    public void EnqueueTask(Vector3i position, int priority)
    {
        if (!_isDisposed && !_inputQueue.IsAddingCompleted)
        {
            try { _inputQueue.Add(new ChunkGenerationTask(position, priority)); }
            catch (InvalidOperationException) { }
        }
    }

    public bool TryGetResult(out ChunkGenerationResult result)
    {
        // Не блокирует Main Thread
        return _outputQueue.TryTake(out result);
    }

    public void SetThreadCount(int count)
    {
        if (_isDisposed) return;
        _threadsCts.Cancel();
        foreach (var t in _threads) if (t.IsAlive) t.Join(50);
        _threads.Clear();

        _threadsCts.Dispose();
        _threadsCts = new CancellationTokenSource();

        for (int i = 0; i < count; i++)
        {
            var t = new Thread(() => WorkerLoop(_threadsCts.Token))
            {
                IsBackground = true, Priority = ThreadPriority.Lowest, Name = $"GenThread_{i}"
            };
            t.Start();
            _threads.Add(t);
        }
    }

    private void WorkerLoop(CancellationToken token)
    {
        try
        {
            foreach (var task in _inputQueue.GetConsumingEnumerable(token))
            {
                // Используем Volume из Constants
                var voxels = System.Buffers.ArrayPool<MaterialType>.Shared.Rent(Constants.ChunkVolume);
                
                long start = Stopwatch.GetTimestamp();
                _generator.GenerateChunk(task.Position, voxels);
                long end = Stopwatch.GetTimestamp();

                if (PerformanceMonitor.IsEnabled) PerformanceMonitor.Record(ThreadType.Generation, end - start);

                try
                {
                    // Блокируется, если очередь полна
                    _outputQueue.Add(new ChunkGenerationResult(task.Position, voxels), token);
                }
                catch (OperationCanceledException)
                {
                    System.Buffers.ArrayPool<MaterialType>.Shared.Return(voxels);
                    break;
                }
                catch (InvalidOperationException)
                {
                    System.Buffers.ArrayPool<MaterialType>.Shared.Return(voxels);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"[Gen] Error: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _threadsCts.Cancel();
        _inputQueue.CompleteAdding();

        foreach (var t in _threads) if (t.IsAlive) t.Join(100);

        while (_outputQueue.TryTake(out var leftovers))
            System.Buffers.ArrayPool<MaterialType>.Shared.Return(leftovers.Voxels);

        _inputQueue.Dispose();
        _outputQueue.Dispose();
        _threadsCts.Dispose();
    }
}