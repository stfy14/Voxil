using System;

public enum ShadowMode
{
    None,
    Hard,
    Soft
}

public static class GameSettings
{
    // Дальность прорисовки (в чанках)
    public static int RenderDistance = 32;
    
    // Лимит загрузки чанков в GPU за кадр.
    // Было 1000. Ставим 2000, чтобы быстрее заливать готовые чанки.
    public static int GpuUploadSpeed = 2000;
    
    // Кол-во потоков генерации мира (Bepu).
    public static int GenerationThreads = 1;
    
    // Кол-во потоков физики (Bepu). 
    public static int PhysicsThreads = 1;
    
    // Тип воды: true = Procedural (Gerstner), false = Texture
    public static bool UseProceduralWater = false; 
    public static bool EnableWaterTransparency = false; 

    // --- НОВЫЕ НАСТРОЙКИ ТЕНЕЙ ---
    public static ShadowMode CurrentShadowMode = ShadowMode.None;
    public static bool EnableAO = false;              
    public static int SoftShadowSamples = 8; 
    
    // --- Дебаг оптимизации --- ///
    public static bool BeamOptimization = true;
    public static bool ShowDebugHeatmap = false;
}