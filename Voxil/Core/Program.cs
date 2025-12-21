using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using System;
using System.Runtime;

public static class Program
{
    private static void Main()
    {
        // Оптимизация памяти
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        
        Console.WriteLine("=== Voxil GPU ===");
        Console.WriteLine($"GC Mode: {GCSettings.LatencyMode}");
        Console.WriteLine("Starting...");

        var nativeWindowSettings = new NativeWindowSettings()
        {
            ClientSize = new Vector2i(1280, 720),
            Title = "Voxil [GPU Raycasting]",
            APIVersion = new Version(4, 5),
            Profile = ContextProfile.Core,
            Vsync = VSyncMode.Off 
        };

        try
        {
            using var game = new Game(GameWindowSettings.Default, nativeWindowSettings);
            game.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[FATAL ERROR] {ex.Message}");
            Console.WriteLine($"Stack trace:\n{ex.StackTrace}");
        }

        Console.WriteLine("\nApp terminated.");
    }
}