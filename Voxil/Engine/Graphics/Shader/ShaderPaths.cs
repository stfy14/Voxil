public static class ShaderPaths
{
    private const string Root = "Engine/Graphics/GLSL";

    public static readonly string RaycastVert = $"{Root}/raycast.vert";
    public static readonly string RaycastFrag = $"{Root}/raycast.frag";
    public static readonly string TaaFrag = $"{Root}/taa.frag";
    public static readonly string GridUpdate = $"{Root}/grid_update.comp";
    public static readonly string ClearGrid = $"{Root}/clear_grid.comp";
    public static readonly string EditUpdater = $"{Root}/edit_updater.comp";
    public static readonly string ShadowFrag = $"{Root}/shadow.frag";
    public static readonly string ShadowUpsampleFrag = $"{Root}/shadow_upsample.frag";
    public static readonly string CompositeFrag = $"{Root}/composite.frag";
    public static readonly string VctClipmapBuild = $"{Root}/vct_clipmap_build.comp";

    public static class Textures
    {
        public static readonly string WaterNoise = $"{Root}/Images/water_noise.png";
    }
}