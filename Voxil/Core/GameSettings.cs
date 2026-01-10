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
    public static int RenderDistance = 64;
    public static bool EnableAO = true;    
    public static ShadowMode CurrentShadowMode = ShadowMode.Soft;
    public static int SoftShadowSamples = 8; 
    public static bool UseProceduralWater = false; 
    public static bool EnableWaterTransparency = false; 
    
    // === ОПТИМИЗАЦИЯ ===
    public static bool BeamOptimization = false;
    public static bool EnableLOD = true; 
    public static float LodPercentage = 0.45f;  
    public static bool DisableEffectsOnLOD = true; 

    // === ПОТОКИ И ПРОИЗВОДИТЕЛЬНОСТЬ ===
    public static int GpuUploadSpeed = 5000;     // Количество чанков, отправляемое GPU на рендер за кадр
    public static int GenerationThreads = 2;    // Количество потоков для генерации мира.
    public static int PhysicsThreads = 1;    // Количество потоков для физической симуляции Bepu.
    public static int TargetFPSForBudgeting = 75;    // Целевой FPS, на который ориентируется система бюджета времени.
    public static float WorldUpdateBudgetPercentage = 0.3f;    // Процент от времени кадра, выделяемый на обработку чанков в основном потоке (например, 0.3f = 30%).
    
    // === ДЕБАГ ===
    public static bool ShowDebugHeatmap = false;
}