// /Diagnostics/PerformanceMonitor.cs
using System.Collections.Generic;
using System.Diagnostics;

public enum ThreadType
{
    Generation,
    Meshing,
    Physics,
    Detachment
}

public static class PerformanceMonitor
{
    private static readonly object _lock = new object();

    private static long _generationTimeTotal = 0;
    private static int _generationCount = 0;

    private static long _meshingTimeTotal = 0;
    private static int _meshingCount = 0;

    private static long _physicsTimeTotal = 0;
    private static int _physicsCount = 0;

    private static long _detachmentTimeTotal = 0;
    private static int _detachmentCount = 0;

    /// <summary>
    /// Записывает время выполнения одной задачи. Потокобезопасен.
    /// </summary>
    public static void RecordTiming(ThreadType type, Stopwatch stopwatch)
    {
        lock (_lock)
        {
            switch (type)
            {
                case ThreadType.Generation:
                    _generationTimeTotal += stopwatch.ElapsedMilliseconds;
                    _generationCount++;
                    break;
                case ThreadType.Meshing:
                    _meshingTimeTotal += stopwatch.ElapsedMilliseconds;
                    _meshingCount++;
                    break;
                case ThreadType.Physics:
                    _physicsTimeTotal += stopwatch.ElapsedMilliseconds;
                    _physicsCount++;
                    break;
                case ThreadType.Detachment:
                    _detachmentTimeTotal += stopwatch.ElapsedMilliseconds;
                    _detachmentCount++;
                    break;
            }
        }
    }

    /// <summary>
    /// Возвращает словарь со средними значениями и сбрасывает счетчики. Потокобезопасен.
    /// </summary>
    public static Dictionary<ThreadType, double> GetAveragesAndReset()
    {
        lock (_lock)
        {
            var averages = new Dictionary<ThreadType, double>
            {
                [ThreadType.Generation] = _generationCount == 0 ? 0 : (double)_generationTimeTotal / _generationCount,
                [ThreadType.Meshing] = _meshingCount == 0 ? 0 : (double)_meshingTimeTotal / _meshingCount,
                [ThreadType.Physics] = _physicsCount == 0 ? 0 : (double)_physicsTimeTotal / _physicsCount,
                [ThreadType.Detachment] = _detachmentCount == 0 ? 0 : (double)_detachmentTimeTotal / _detachmentCount
            };

            // Сбрасываем счетчики для следующего интервала
            _generationTimeTotal = 0;
            _generationCount = 0;
            _meshingTimeTotal = 0;
            _meshingCount = 0;
            _physicsTimeTotal = 0;
            _physicsCount = 0;
            _detachmentTimeTotal = 0;
            _detachmentCount = 0;

            return averages;
        }
    }
}