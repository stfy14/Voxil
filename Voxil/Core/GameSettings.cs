public enum ShadowMode
{
    None,
    Hard,
    Soft
}

public static class GameSettings
{
    // Дальность прорисовки (в чанках)
    public static int RenderDistance = 2;
    
    // Лимит загрузки чанков в GPU за кадр
    public static int GpuUploadSpeed = 200;
    
    // Кол-во потоков генерации (Perlin noise)
    public static int GenerationThreads = 1;
    
    // Кол-во потоков физики (Bepu)
    public static int PhysicsThreads = 1;
    
    // Тип воды: true = Procedural (Gerstner), false = Texture
    public static bool UseProceduralWater = false; 
    public static bool EnableWaterTransparency = false; // Вкл/Выкл Прозрачность и Каустику

    // --- НОВЫЕ НАСТРОЙКИ ТЕНЕЙ ---
    
    // Режим теней
    public static ShadowMode CurrentShadowMode = ShadowMode.None;
    public static bool EnableAO = false;              // Вкл/Выкл AO
    // Качество мягких теней (кол-во лучей). 
    // Меньше = быстрее/шумнее, Больше = медленнее/качественнее.
    // Рекомендуемые значения: 4, 8, 16, 32.
    public static int SoftShadowSamples = 8; 
    
    // --- Дебаг оптимизации --- ///
    public static bool BeamOptimization = true;

}