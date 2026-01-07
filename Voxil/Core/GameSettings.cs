public enum ShadowMode
{
    None,
    Hard,
    Soft
}

public static class GameSettings
{
    // Дальность прорисовки (в чанках)
    public static int RenderDistance = 16;
    
    // Лимит загрузки чанков в GPU за кадр
    public static int GpuUploadSpeed = 200;
    
    // Кол-во потоков генерации (Perlin noise)
    public static int GenerationThreads = 1;
    
    // Кол-во потоков физики (Bepu)
    public static int PhysicsThreads = 1;
    
    // Тип воды: true = Procedural (Gerstner), false = Texture
    public static bool UseProceduralWater = false; 

    // --- НОВЫЕ НАСТРОЙКИ ТЕНЕЙ ---
    
    // Режим теней
    public static ShadowMode CurrentShadowMode = ShadowMode.Soft;

    // Качество мягких теней (кол-во лучей). 
    // Меньше = быстрее/шумнее, Больше = медленнее/качественнее.
    // Рекомендуемые значения: 4, 8, 16, 32.
    public static int SoftShadowSamples = 8; 
}