// --- START OF FILE GameSettings.cs ---
using System;

public enum ShadowMode { None, Hard, Soft }

public static class GameSettings
{
    // === ДЛЯ EDITOR ===
    public static bool IsEditorMode { get; set; } = false;

    // === ВРЕМЯ И ЦИКЛ ДНЯ/НОЧИ ===
    public static bool EnableDynamicTime = true;
    public static double TotalTimeHours = 12.0;
    public static float TimeOfDay => (float)(TotalTimeHours % 24.0);
    public static float TimeScale = 120.0f;

    // === ГРАФИКА ===
    public static int RenderDistance = 32;
    public static float RenderScale = 1.0f;
    public static bool EnableAO = true;
    public static bool EnableTAA = true;
    public static ShadowMode CurrentShadowMode = ShadowMode.Hard;
    public static int SoftShadowSamples = 8;
    public static bool UseProceduralWater = false;
    public static bool EnableWaterTransparency = false;

    // === GLOBAL ILLUMINATION ===
    public static bool EnableGI = true;
    public static bool ShowGIProbes = false;
    public static bool ShowGIProbesXRay = false;
    public static bool ShowGIProbeGridBounds = false; // <--- НОВОЕ ПОЛЕ (границы каскадов)

    // === ОПТИМИЗАЦИЯ ===
    public static bool BeamOptimization = true;
    public static bool EnableLOD = true;
    public static float LodPercentage = 0.85f;
    public static bool DisableEffectsOnLOD = true;
    public static int ShadowDownscale = 2;

    // === ПОТОКИ И ПРОИЗВОДИТЕЛЬНОСТЬ ===
    public static int GpuUploadSpeed = 10000;
    public static int GenerationThreads = 2;
    public static int PhysicsThreads = 1;
    public static int TargetFPSForBudgeting = 60;
    public static float WorldUpdateBudgetPercentage = 0.3f;

    // === ДЕБАГ ===
    public static bool ShowDebugHeatmap = false;
    public static bool ShowStaticCollisions = false;
    public static bool ShowDynamicCollisions = false;
    public static bool ShowExplosionRays = false;
    public static bool ShowExplosionRadius = false;
    public static int GIDebugLOD = 0;
}