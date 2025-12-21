using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

public enum ThreadType
{
    Generation,     // Генерация чанка (в среднем на 1 чанк)
    Physics,        // Физика (общее время за кадр)
    GpuRender       // Подготовка данных для GPU
}

public static class PerformanceMonitor
{
    // Глобальный рубильник. Volatile гарантирует, что потоки увидят изменение сразу.
    public static volatile bool IsEnabled = false;

    // Храним тики (long), так как Interlocked работает с ними быстро
    private static long _genTicks; private static int _genCount;
    private static long _physTicks; private static int _physCount;
    private static long _gpuTicks; private static int _gpuCount;

    // Частота таймера для перевода в миллисекунды
    private static readonly double _tickFrequency = Stopwatch.Frequency;

    public static void Record(ThreadType type, long elapsedTicks)
    {
        // Если выключено - сразу выходим (хотя проверка должна быть и снаружи)
        if (!IsEnabled) return;

        switch (type)
        {
            case ThreadType.Generation:
                Interlocked.Add(ref _genTicks, elapsedTicks);
                Interlocked.Increment(ref _genCount);
                break;
            case ThreadType.Physics:
                Interlocked.Add(ref _physTicks, elapsedTicks);
                Interlocked.Increment(ref _physCount);
                break;
            case ThreadType.GpuRender:
                Interlocked.Add(ref _gpuTicks, elapsedTicks);
                Interlocked.Increment(ref _gpuCount);
                break;
        }
    }

    public static Dictionary<string, string> GetDataAndReset()
    {
        if (!IsEnabled) return null;

        // Забираем значения атомарно, обнуляя счетчики
        long genT = Interlocked.Exchange(ref _genTicks, 0);
        int genC = Interlocked.Exchange(ref _genCount, 0);

        long physT = Interlocked.Exchange(ref _physTicks, 0);
        int physC = Interlocked.Exchange(ref _physCount, 0);

        long gpuT = Interlocked.Exchange(ref _gpuTicks, 0);
        int gpuC = Interlocked.Exchange(ref _gpuCount, 0);

        // Вспомогательная функция перевода
        string FormatMs(long ticks, int count)
        {
            if (count == 0) return "0.0 ms";
            // Среднее время = Всего тиков / Кол-во вызовов
            double avgTicks = (double)ticks / count;
            double ms = (avgTicks / _tickFrequency) * 1000.0;
            return $"{ms:F2} ms";
        }
        
        // Для физики и GPU нас интересует суммарное время за кадр (или за интервал замера),
        // но так как мы сбрасываем счетчик раз в 0.2с, лучше показывать среднее время одной операции.
        // Для физики это "шаг симуляции", для GPU это "кадр".

        return new Dictionary<string, string>
        {
            ["Gen (avg/chunk)"] = FormatMs(genT, genC),
            ["Physics (step)"]  = FormatMs(physT, physC),
            ["GPU Upload"]      = FormatMs(gpuT, gpuC)
        };
    }
}