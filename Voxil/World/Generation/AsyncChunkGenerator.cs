// --- START OF FILE AsyncChunkGenerator.cs ---

using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

// --- Вспомогательный класс BlockingPriorityQueue остается без изменений ---
public class BlockingPriorityQueue<T>
{
    private readonly PriorityQueue<T, int> _queue = new PriorityQueue<T, int>();
    private readonly object _lock = new object();
    private bool _isCompleted = false;
    public int Count { get { lock (_lock) return _queue.Count; } }
    public void Enqueue(T item, int priority) { lock (_lock) { if (_isCompleted) return; _queue.Enqueue(item, priority); Monitor.Pulse(_lock); } }
    public bool TryDequeue(out T result, CancellationToken token) { lock (_lock) { while (_queue.Count == 0) { if (_isCompleted || token.IsCancellationRequested) { result = default; return false; } Monitor.Wait(_lock); } if (token.IsCancellationRequested) { result = default; return false; } return _queue.TryDequeue(out result, out _); } }
    public void Clear() { lock (_lock) { _queue.Clear(); } }
    public void CompleteAdding() { lock (_lock) { _isCompleted = true; Monitor.PulseAll(_lock); } }
}


public class AsyncChunkGenerator : IDisposable
{
    // === НОВАЯ КОНСТАНТА ===
    // Сколько чанков должен сгенерировать один поток перед отправкой в очередь.
    // Увеличивает локальную работу, уменьшает борьбу за общую очередь.
    // 4 или 8 - хорошие начальные значения.
    private const int BatchSize = 4;

    private readonly IWorldGenerator _generator;
    private readonly BlockingPriorityQueue<ChunkGenerationTask> _inputQueue = new();
    private readonly BlockingCollection<ChunkGenerationResult> _outputQueue = new(new ConcurrentQueue<ChunkGenerationResult>(), 256); // Увеличим буфер
    private readonly List<Thread> _threads = new();
    private CancellationTokenSource _threadsCts = new();
    private volatile bool _isDisposed;

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

    // === ОБНОВЛЕННЫЙ WorkerLoop С БАТЧИНГОМ ===
    private void WorkerLoop(CancellationToken token)
    {
        // Локальный список для хранения результатов. Потоку не нужно его ни с кем делить.
        var localResults = new List<ChunkGenerationResult>(BatchSize);

        while (!token.IsCancellationRequested)
        {
            try
            {
                // 1. Набираем пакет задач
                for (int i = 0; i < BatchSize; i++)
                {
                    // Пытаемся взять задачу из общей очереди (здесь все еще есть блокировка)
                    if (!_inputQueue.TryDequeue(out var task, token))
                        break; // Задачи кончились, выходим из внутреннего цикла

                    // 2. Выполняем работу
                    var voxels = System.Buffers.ArrayPool<MaterialType>.Shared.Rent(Constants.ChunkVolume);
                    long start = Stopwatch.GetTimestamp();
                    _generator.GenerateChunk(task.Position, voxels);
                    long end = Stopwatch.GetTimestamp();
                    if (PerformanceMonitor.IsEnabled) 
                        PerformanceMonitor.Record(ThreadType.Generation, end - start);

                    // 3. Кладем результат в ЛОКАЛЬНЫЙ список. Никаких блокировок.
                    localResults.Add(new ChunkGenerationResult(task.Position, voxels));
                }

                // 4. Если мы сгенерировали хотя бы один чанк...
                if (localResults.Count > 0)
                {
                    // 5. ...только теперь мы ОДИН РАЗ обращаемся к общей очереди
                    // и выгружаем туда весь наш пакет.
                    foreach (var result in localResults)
                    {
                        _outputQueue.Add(result, token);
                    }
                    localResults.Clear(); // Очищаем локальный список для следующего пакета
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) 
            { 
                Console.WriteLine($"[Gen] Error: {ex.Message}");
                // Чистим локальные результаты в случае ошибки, чтобы не отправить мусор
                foreach (var result in localResults)
                {
                     if(result.Voxels != null) System.Buffers.ArrayPool<MaterialType>.Shared.Return(result.Voxels);
                }
                localResults.Clear();
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