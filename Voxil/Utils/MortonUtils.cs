using OpenTK.Mathematics;

public static class MortonUtils
{
    // "Разделяет" биты одного 32-битного числа, вставляя два 0 между каждым битом.
    // Пример: 1011 -> 001 000 001 001
    private static ulong SplitBy2(uint a)
    {
        ulong x = a;
        x = (x | (x << 32)) & 0x00000000ffffffff;
        x = (x | (x << 16)) & 0x0000ffff0000ffff;
        x = (x | (x << 8))  & 0x00ff00ff00ff00ff;
        x = (x | (x << 4))  & 0x0f0f0f0f0f0f0f0f;
        x = (x | (x << 2))  & 0x3333333333333333;
        x = (x | (x << 1))  & 0x5555555555555555;
        return x;
    }

    /// <summary>
    /// Кодирует 3D-координаты (x, y, z) в один 64-битный код Мортона.
    /// </summary>
    public static ulong Encode(Vector3i pos)
    {
        return SplitBy2((uint)pos.X) | (SplitBy2((uint)pos.Y) << 1) | (SplitBy2((uint)pos.Z) << 2);
    }
}