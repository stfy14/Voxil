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
}