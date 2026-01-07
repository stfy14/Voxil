using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

public class AsyncChunkGenerator : IDisposable
{
    private readonly IWorldGenerator _generator;

    // ВХОДНАЯ очередь (задачи). Без лимита (или большой лимит), чтобы быстро накидать задач.
    private readonly BlockingCollection<ChunkGenerationTask> _inputQueue = new(new ConcurrentQueue<ChunkGenerationTask>());

    // ВЫХОДНАЯ очередь (результаты). 
    // !!! ВАЖНО !!! boundedCapacity: 64. 
    // Если здесь будет 64 готовых чанка, потоки-генераторы САМИ уснут (Block), пока Main Thread не заберет данные.
    // Это полностью устраняет переполнение памяти.
    private readonly BlockingCollection<ChunkGenerationResult> _outputQueue = new(new ConcurrentQueue<ChunkGenerationResult>(), boundedCapacity: 64);

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
        if (_isDisposed || _inputQueue.IsAddingCompleted) return;

        try
        {
            _inputQueue.Add(new ChunkGenerationTask(position, priority));
        }
        catch (InvalidOperationException) { }
    }

    // Main Thread вызывает это. Это не блокирует Main Thread (т.к. TryTake).
    public bool TryGetResult(out ChunkGenerationResult result)
    {
        // TryTake забирает элемент. Если очередь была полна (64), 
        // это действие разблокирует один из потоков генерации.
        return _outputQueue.TryTake(out result);
    }

    public void SetThreadCount(int count)
    {
        if (_isDisposed) return;

        // 1. Мягкая остановка
        _threadsCts.Cancel();

        // Ждем совсем чуть-чуть, чтобы потоки успели понять, что пора на выход
        foreach (var t in _threads) if (t.IsAlive) t.Join(50);
        _threads.Clear();

        // 2. Новый токен
        _threadsCts.Dispose();
        _threadsCts = new CancellationTokenSource();

        // 3. Запуск
        for (int i = 0; i < count; i++)
        {
            var t = new Thread(() => WorkerLoop(_threadsCts.Token))
            {
                IsBackground = true,
                Priority = ThreadPriority.Lowest, // Чтобы не фризить игру
                Name = $"GenThread_{i}"
            };
            t.Start();
            _threads.Add(t);
        }

        Console.WriteLine($"[Generator] Threads: {count}. Output Buffer Limit: {_outputQueue.BoundedCapacity}");
    }

    private void WorkerLoop(CancellationToken token)
    {
        try
        {
            // GetConsumingEnumerable() — это "Золотой стандарт" обработки очередей в .NET.
            // Он сам делает TryTake, сам ждет, сам проверяет CancellationToken.
            // Цикл прервется, если:
            // 1. Вызван _inputQueue.CompleteAdding() (при Dispose)
            // 2. Отменен token (при смене кол-ва потоков)
            foreach (var task in _inputQueue.GetConsumingEnumerable(token))
            {
                // ГЕНЕРАЦИЯ
                // Арендуем массив. Теперь мы не арендуем 200к массивов, 
                // потому что _outputQueue.Add заблокирует нас на 64-м массиве.
                var voxels = System.Buffers.ArrayPool<MaterialType>.Shared.Rent(Chunk.Volume);

                long start = Stopwatch.GetTimestamp();
                _generator.GenerateChunk(task.Position, voxels);
                long end = Stopwatch.GetTimestamp();

                if (PerformanceMonitor.IsEnabled)
                    PerformanceMonitor.Record(ThreadType.Generation, end - start);

                // ОТПРАВКА
                try
                {
                    // !!! ГЛАВНОЕ МЕСТО !!!
                    // Пытаемся добавить в выходную очередь.
                    // Если там уже 64 элемента, этот вызов ЗАБЛОКИРУЕТ поток (поток уснет).
                    // Он проснется только тогда, когда Main Thread заберет чанк.
                    // Также передаем token, чтобы если игру закрыли, мы вышли из ожидания.
                    _outputQueue.Add(new ChunkGenerationResult(task.Position, voxels), token);
                }
                catch (OperationCanceledException)
                {
                    // Если во время ожидания (пока очередь полна) отменили токен - возвращаем массив и выходим
                    System.Buffers.ArrayPool<MaterialType>.Shared.Return(voxels);
                    break;
                }
                catch (InvalidOperationException)
                {
                    // Если выходную очередь пометили как завершенную
                    System.Buffers.ArrayPool<MaterialType>.Shared.Return(voxels);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Штатный выход из GetConsumingEnumerable при отмене токена
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Gen] Error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // 1. Отменяем токен (будит потоки, висящие в _outputQueue.Add или в ожидании задачи)
        _threadsCts.Cancel();

        // 2. Закрываем входную очередь
        _inputQueue.CompleteAdding();

        // 3. Ждем завершения
        foreach (var t in _threads)
        {
            if (t.IsAlive) t.Join(100);
        }

        // 4. Очищаем хвосты (если остались данные в output, надо вернуть массивы в пул)
        while (_outputQueue.TryTake(out var leftovers))
        {
            System.Buffers.ArrayPool<MaterialType>.Shared.Return(leftovers.Voxels);
        }

        _inputQueue.Dispose();
        _outputQueue.Dispose();
        _threadsCts.Dispose();
    }
}