using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

// --- Вспомогательный класс очереди с приоритетом ---
public class BlockingPriorityQueue<T>
{
    // Используем стандартный PriorityQueue (.NET 6+)
    private readonly PriorityQueue<T, int> _queue = new PriorityQueue<T, int>();
    private readonly object _lock = new object();
    private bool _isCompleted = false;

    // Свойство для мониторинга количества задач
    public int Count
    {
        get { lock (_lock) return _queue.Count; }
    }

    public void Enqueue(T item, int priority)
    {
        lock (_lock)
        {
            if (_isCompleted) return;
            _queue.Enqueue(item, priority);
            Monitor.Pulse(_lock); // Будим поток
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
                Monitor.Wait(_lock); // Ждем задачи
            }

            if (token.IsCancellationRequested)
            {
                result = default;
                return false;
            }

            return _queue.TryDequeue(out result, out _);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
        }
    }

    public void CompleteAdding()
    {
        lock (_lock)
        {
            _isCompleted = true;
            Monitor.PulseAll(_lock);
        }
    }
}

// --- Основной класс генератора ---
public class AsyncChunkGenerator : IDisposable
{
    private readonly IWorldGenerator _generator;
    
    // Входная очередь (с приоритетом)
    private readonly BlockingPriorityQueue<ChunkGenerationTask> _inputQueue = new();
    
    // Выходная очередь (обычная)
    private readonly BlockingCollection<ChunkGenerationResult> _outputQueue = new(new ConcurrentQueue<ChunkGenerationResult>(), 64);
    
    private readonly List<Thread> _threads = new();
    private CancellationTokenSource _threadsCts = new();
    private volatile bool _isDisposed;

    // Публичные счетчики для DebugStats
    public int PendingCount => _inputQueue.Count;
    public int ResultsCount => _outputQueue.Count;

    public AsyncChunkGenerator(int seed, int threadCount)
    {
        _generator = new PerlinGenerator(seed);
        SetThreadCount(threadCount);
    }

    public void EnqueueTask(Vector3i position, int priority)
    {
        if (!_isDisposed)
        {
            _inputQueue.Enqueue(new ChunkGenerationTask(position, priority), priority);
        }
    }

    public bool TryGetResult(out ChunkGenerationResult result)
    {
        return _outputQueue.TryTake(out result);
    }

    public void ClearQueue()
    {
        _inputQueue.Clear();
        // Очищаем выходную очередь и возвращаем память в пул
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
                IsBackground = true, 
                Priority = ThreadPriority.BelowNormal, 
                Name = $"GenThread_{i}"
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
                // Если задач нет, поток уснет здесь (Wait)
                if (!_inputQueue.TryDequeue(out var task, token)) 
                    break;

                voxels = System.Buffers.ArrayPool<MaterialType>.Shared.Rent(Constants.ChunkVolume);
                
                long start = Stopwatch.GetTimestamp();
                _generator.GenerateChunk(task.Position, voxels);
                long end = Stopwatch.GetTimestamp();

                if (PerformanceMonitor.IsEnabled) 
                    PerformanceMonitor.Record(ThreadType.Generation, end - start);

                _outputQueue.Add(new ChunkGenerationResult(task.Position, voxels), token);
                voxels = null; // Сброс ссылки, т.к. массив ушел в очередь
            }
            catch (OperationCanceledException) 
            {
                break; 
            }
            catch (Exception ex) 
            { 
                Console.WriteLine($"[Gen] Error: {ex.Message}");
                // В случае ошибки отправляем пустой результат, чтобы система знала, что задача завершена
                try { _outputQueue.Add(new ChunkGenerationResult(default, null), token); } catch {}
            }
            finally
            {
                // Если voxels остался не null (например, ошибка до добавления в очередь), возвращаем его
                if (voxels != null) 
                {
                    System.Buffers.ArrayPool<MaterialType>.Shared.Return(voxels); 
                    voxels = null; 
                }
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

        ClearQueue(); // Чистим остатки

        _outputQueue.Dispose();
        _threadsCts.Dispose();
    }
}