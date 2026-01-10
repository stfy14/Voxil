// --- START OF FILE GameSettings.cs ---

using System;

public enum ShadowMode
{
    None,
    Hard,
    Soft
}

public static class GameSettings
{
    // === ГРАФИКА ===
    public static int RenderDistance = 32;
    public static int GpuUploadSpeed = 2000;
    public static bool UseProceduralWater = false; 
    public static bool EnableWaterTransparency = false; 
    public static ShadowMode CurrentShadowMode = ShadowMode.None;
    public static bool EnableAO = false;              
    public static int SoftShadowSamples = 8; 
    
    // === ОПТИМИЗАЦИЯ ===
    public static bool BeamOptimization = true;
    public static bool ShowDebugHeatmap = false;

    // === ПОТОКИ И ПРОИЗВОДИТЕЛЬНОСТЬ (CPU) ===
    
    // Количество потоков для генерации мира.
    public static int GenerationThreads = 2;
    
    // Количество потоков для физической симуляции Bepu.
    public static int PhysicsThreads = 1;
    
    // --- НОВЫЕ НАСТРОЙКИ БЮДЖЕТА ОСНОВНОГО ПОТОКА ---
    
    // Целевой FPS, на который ориентируется система бюджета времени.
    // Система будет стараться не превышать время кадра для этого FPS.
    public static int TargetFPSForBudgeting = 75;
    
    // Процент от времени кадра, выделяемый на обработку чанков в основном потоке (например, 0.3f = 30%).
    // Уменьшение этого значения может сделать загрузку мира чуть медленнее, но игра будет более отзывчивой.
    public static float WorldUpdateBudgetPercentage = 0.3f;
}