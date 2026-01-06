using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

public enum ThreadType
{
    Generation,     // Генерация ландшафта (Perlin)
    Physics,        // Шаг симуляции BepuPhysics (Update)
    ChunkPhys,      // Построение коллайдеров чанка (Meshing) <--- НОВОЕ
    GpuRender       // Upload данных в GPU
}

public static class PerformanceMonitor
{
    public static volatile bool IsEnabled = true;

    private static long _genTicks; private static int _genCount;
    private static long _physTicks; private static int _physCount; // Bepu Step
    private static long _cPhysTicks; private static int _cPhysCount; // Chunk Collider Build
    private static long _gpuTicks; private static int _gpuCount;

    private static readonly double _tickFrequency = Stopwatch.Frequency;

    public static void Record(ThreadType type, long elapsedTicks)
    {
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
            case ThreadType.ChunkPhys: // <--- НОВОЕ
                Interlocked.Add(ref _cPhysTicks, elapsedTicks);
                Interlocked.Increment(ref _cPhysCount);
                break;
            case ThreadType.GpuRender:
                Interlocked.Add(ref _gpuTicks, elapsedTicks);
                Interlocked.Increment(ref _gpuCount);
                break;
        }
    }

    // Теперь принимаем время, прошедшее с прошлого замера
    public static Dictionary<string, string> GetDataAndReset(double elapsedSeconds)
    {
        if (!IsEnabled) return null;

        long genT = Interlocked.Exchange(ref _genTicks, 0);
        int genC = Interlocked.Exchange(ref _genCount, 0);

        long physT = Interlocked.Exchange(ref _physTicks, 0);
        int physC = Interlocked.Exchange(ref _physCount, 0);
        
        long cPhysT = Interlocked.Exchange(ref _cPhysTicks, 0);
        int cPhysC = Interlocked.Exchange(ref _cPhysCount, 0);

        long gpuT = Interlocked.Exchange(ref _gpuTicks, 0);
        int gpuC = Interlocked.Exchange(ref _gpuCount, 0);

        // Форматирование: Время выполнения (ms) | CPS (Chunks/Calls per Second)
        string FormatStat(long ticks, int count, bool showCps)
        {
            if (count == 0) return "0.0 ms | 0.0/s";
            
            double avgTicks = (double)ticks / count;
            double ms = (avgTicks / _tickFrequency) * 1000.0;
            
            if (showCps && elapsedSeconds > 0.0001)
            {
                double cps = (double)count / elapsedSeconds;
                return $"{ms:F2} ms | {cps:F1}/s";
            }
            
            return $"{ms:F2} ms";
        }

        return new Dictionary<string, string>
        {
            ["Gen (Perlin)"]   = FormatStat(genT, genC, true),  // Покажет CPS генерации
            ["Build (Phys)"]   = FormatStat(cPhysT, cPhysC, true), // Покажет CPS мешинга
            ["Sim (Bepu)"]     = FormatStat(physT, physC, false), // Просто время шага
            ["GPU Upload"]     = FormatStat(gpuT, gpuC, true)   // Покажет скорость заливки
        };
    }
}