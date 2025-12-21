public static class GameSettings
{
    // Дальность прорисовки (в чанках)
    public static int RenderDistance = 64;
    
    // Лимит загрузки чанков в GPU за кадр
    public static int GpuUploadSpeed = 200;
    
    // Кол-во потоков генерации (Perlin noise)
    public static int GenerationThreads = 1;
    
    // Кол-во потоков физики (Bepu)
    public static int PhysicsThreads = 1;
}