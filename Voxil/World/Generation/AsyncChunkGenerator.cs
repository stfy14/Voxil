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
    private readonly ConcurrentQueue<ChunkGenerationResult> _outputQueue = new();
    
    private readonly List<Thread> _threads = new();
    private CancellationTokenSource _cts = new();
    private bool _isDisposed;

    public AsyncChunkGenerator(int seed, int threadCount)
    {
        _generator = new PerlinGenerator(seed);
        SetThreadCount(threadCount);
    }

    public void EnqueueTask(Vector3i position, int priority)
    {
        if (!_isDisposed)
            _inputQueue.Add(new ChunkGenerationTask(position, priority));
    }

    public bool TryGetResult(out ChunkGenerationResult result)
    {
        return _outputQueue.TryDequeue(out result);
    }

    public void SetThreadCount(int count)
    {
        _cts.Cancel();
        foreach (var t in _threads) if (t.IsAlive) t.Join(10);
        _threads.Clear();
        _cts = new CancellationTokenSource();

        for (int i = 0; i < count; i++)
        {
            var t = new Thread(() => WorkerLoop(_cts.Token))
            {
                IsBackground = true,
                Priority = ThreadPriority.Lowest,
                Name = $"GenThread_{i}"
            };
            t.Start();
            _threads.Add(t);
        }
    }

    private void WorkerLoop(CancellationToken token)
    {
        while (!_isDisposed && !token.IsCancellationRequested)
        {
            try
            {
                if (_inputQueue.TryTake(out var task, 100, token))
                {
                    // Арендуем массив, чтобы не мусорить памятью
                    var voxels = System.Buffers.ArrayPool<MaterialType>.Shared.Rent(Chunk.Volume);
                    
                    long start = Stopwatch.GetTimestamp();
                    _generator.GenerateChunk(task.Position, voxels);
                    long end = Stopwatch.GetTimestamp();

                    if (PerformanceMonitor.IsEnabled)
                        PerformanceMonitor.Record(ThreadType.Generation, end - start);

                    _outputQueue.Enqueue(new ChunkGenerationResult(task.Position, voxels));
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[Gen] Error: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        _cts.Cancel();
        _inputQueue.CompleteAdding();
        foreach (var t in _threads) if (t.IsAlive) t.Join(50);
        _inputQueue.Dispose();
    }
}