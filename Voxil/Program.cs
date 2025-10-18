// Program.cs
using OpenTK.Windowing.Desktop;
using OpenTK.Mathematics;
using System;

public static class Program
{
    private static void Main()
    {
        Console.WriteLine("=== Voxil ===");
        Console.WriteLine("Запуск приложения...\n");

        var nativeWindowSettings = new NativeWindowSettings()
        {
            ClientSize = new Vector2i(1280, 720),
            Title = "Voxil",
            APIVersion = new Version(3, 3),
        };

        try
        {
            using var game = new Game(GameWindowSettings.Default, nativeWindowSettings);
            game.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[КРИТИЧЕСКАЯ ОШИБКА] {ex.Message}");
            Console.WriteLine($"Stack trace:\n{ex.StackTrace}");
        }

        Console.WriteLine("\nПриложение завершено.");
    }
}