// --- START OF FILE GameSettings.cs ---

using System;

public enum ShadowMode
{
    None,
    Hard,
    Soft
}

// Новый Enum для режима отладки коллизий
public enum CollisionDebugMode
{
    None,
    PhysicsOnly, // Динамика
    StaticOnly,  // Чанки
    All
}

public static class GameSettings
{
    // === ГРАФИКА ===
    public static int RenderDistance = 32;
    public static bool EnableAO = true;    
    public static ShadowMode CurrentShadowMode = ShadowMode.Soft;
    public static int SoftShadowSamples = 8; 
    public static bool UseProceduralWater = false; 
    public static bool EnableWaterTransparency = false; 
    
    // === ОПТИМИЗАЦИЯ ===
    public static bool BeamOptimization = true;
    public static bool EnableLOD = true; 
    public static float LodPercentage = 0.70f;  
    public static bool DisableEffectsOnLOD = true; 

    // === ПОТОКИ И ПРОИЗВОДИТЕЛЬНОСТЬ ===
    public static int GpuUploadSpeed = 10000;
    public static int GenerationThreads = 2;
    public static int PhysicsThreads = 1;
    public static int TargetFPSForBudgeting = 60;
    public static float WorldUpdateBudgetPercentage = 0.3f;
    
    // === ДЕБАГ ===
    public static bool ShowDebugHeatmap = false;
    public static CollisionDebugMode DebugCollisionMode = CollisionDebugMode.None; // <--- Новая настройка
}