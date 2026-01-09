using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

// Очередь с приоритетом (меньшее число = выше приоритет)
public class BlockingPriorityQueue<T>
{
    private readonly PriorityQueue<T, int> _queue = new PriorityQueue<T, int>();
    private readonly object _lock = new object();
    private bool _isCompleted = false;

    // Свойство для проверки размера очереди
    public int Count { get { lock(_lock) return _queue.Count; } }

    public void Enqueue(T item, int priority)
    {
        lock (_lock)
        {
            if (_isCompleted) return;
            _queue.Enqueue(item, priority);
            Monitor.Pulse(_lock);
        }
    }

    public bool TryDequeue(out T result, CancellationToken token)
    {
        lock (_lock)
        {
            while (_queue.Count == 0)
            {
                if (_isCompleted || token.IsCancellationRequested)
                {
                    result = default;
                    return false;
                }
                Monitor.Wait(_lock);
            }

            if (token.IsCancellationRequested)
            {
                result = default;
                return false;
            }

            return _queue.TryDequeue(out result, out _);
        }
    }

    public void Clear() { lock (_lock) { _queue.Clear(); } }
    public void CompleteAdding() { lock (_lock) { _isCompleted = true; Monitor.PulseAll(_lock); } }
}

public class AsyncChunkGenerator : IDisposable
{
    private readonly IWorldGenerator _generator;
    
    // Входная очередь с приоритетом
    private readonly BlockingPriorityQueue<ChunkGenerationTask> _inputQueue = new();
    
    // Выходная очередь
    private readonly BlockingCollection<ChunkGenerationResult> _outputQueue = new(new ConcurrentQueue<ChunkGenerationResult>(), 64);
    
    private readonly List<Thread> _threads = new();
    private CancellationTokenSource _threadsCts = new();
    private volatile bool _isDisposed;

    // Публичный счетчик задач в ожидании
    public int PendingCount => _inputQueue.Count;

    public AsyncChunkGenerator(int seed, int threadCount)
    {
        _generator = new PerlinGenerator(seed);
        SetThreadCount(threadCount);
    }

    public void EnqueueTask(Vector3i position, int priority)
    {
        if (!_isDisposed)
            _inputQueue.Enqueue(new ChunkGenerationTask(position, priority), priority);
    }

    public bool TryGetResult(out ChunkGenerationResult result) => _outputQueue.TryTake(out result);

    public void ClearQueue()
    {
        _inputQueue.Clear();
        while (_outputQueue.TryTake(out var result))
        {
            if (result.Voxels != null)
                System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels);
        }
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
                IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"GenThread_{i}"
            };
            t.Start();
            _threads.Add(t);
        }
    }

    private void WorkerLoop(CancellationToken token)
    {
        MaterialType[] voxels = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Если очередь пуста, эта строка заблокирует поток и он уснет.
                // Это нормально и правильно. CPU будет на 0%.
                if (!_inputQueue.TryDequeue(out var task, token)) break;

                // Если проснулись, значит есть задача. Работаем.
                voxels = System.Buffers.ArrayPool<MaterialType>.Shared.Rent(Constants.ChunkVolume);
                
                long start = Stopwatch.GetTimestamp();
                _generator.GenerateChunk(task.Position, voxels);
                long end = Stopwatch.GetTimestamp();

                if (PerformanceMonitor.IsEnabled) PerformanceMonitor.Record(ThreadType.Generation, end - start);

                _outputQueue.Add(new ChunkGenerationResult(task.Position, voxels), token);
                voxels = null; 
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) 
            { 
                Console.WriteLine($"[Gen] Error: {ex.Message}");
                try { _outputQueue.Add(new ChunkGenerationResult(default, null), token); } catch {}
            }
            finally
            {
                if (voxels != null) { System.Buffers.ArrayPool<MaterialType>.Shared.Return(voxels); voxels = null; }
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _threadsCts.Cancel();
        _inputQueue.CompleteAdding();
        foreach (var t in _threads) if (t.IsAlive) t.Join(100);
        ClearQueue();
        _outputQueue.Dispose();
        _threadsCts.Dispose();
    }
}