// /World/Generation/PerlinNoise.cs
using System;

public class PerlinNoise
{
    private readonly int[] _p;

    public PerlinNoise(int seed)
    {
        var random = new Random(seed);
        var permutation = new int[256];
        for (int i = 0; i < 256; i++)
        {
            permutation[i] = i;
        }

        for (int i = 0; i < 256; i++)
        {
            int source = random.Next(256);
            (permutation[i], permutation[source]) = (permutation[source], permutation[i]);
        }

        _p = new int[512];
        for (int i = 0; i < 256; i++)
        {
            _p[i] = _p[i + 256] = permutation[i];
        }
    }

    // --- 2D ВЕРСИЯ МЕТОДА (ДЛЯ ГЕНЕРАЦИИ ЛАНДШАФТА) ---
    public double Noise(double x, double y)
    {
        int X = (int)Math.Floor(x) & 255;
        int Y = (int)Math.Floor(y) & 255;

        x -= Math.Floor(x);
        y -= Math.Floor(y);

        double u = Fade(x);
        double v = Fade(y);

        int a = _p[X] + Y, b = _p[X + 1] + Y;

        return Lerp(v, Lerp(u, Grad(_p[a], x, y), Grad(_p[b], x - 1, y)),
                       Lerp(u, Grad(_p[a + 1], x, y - 1), Grad(_p[b + 1], x - 1, y - 1)));
    }

    // --- 3D ВЕРСИЯ МЕТОДА (ДЛЯ ПЕЩЕР ИЛИ ОБЪЕМНОГО ШУМА) ---
    public double Noise(double x, double y, double z)
    {
        int X = (int)Math.Floor(x) & 255;
        int Y = (int)Math.Floor(y) & 255;
        int Z = (int)Math.Floor(z) & 255;

        x -= Math.Floor(x);
        y -= Math.Floor(y);
        z -= Math.Floor(z);

        double u = Fade(x);
        double v = Fade(y);
        double w = Fade(z);

        int a = _p[X] + Y, aa = _p[a] + Z, ab = _p[a + 1] + Z;
        int b = _p[X + 1] + Y, ba = _p[b] + Z, bb = _p[b + 1] + Z;

        return Lerp(w, Lerp(v, Lerp(u, Grad(_p[aa], x, y, z),
                                      Grad(_p[ba], x - 1, y, z)),
                               Lerp(u, Grad(_p[ab], x, y - 1, z),
                                      Grad(_p[bb], x - 1, y - 1, z))),
                       Lerp(v, Lerp(u, Grad(_p[aa + 1], x, y, z - 1),
                                      Grad(_p[ba + 1], x - 1, y, z - 1)),
                               Lerp(u, Grad(_p[ab + 1], x, y - 1, z - 1),
                                      Grad(_p[bb + 1], x - 1, y - 1, z - 1))));
    }


    // --- Вспомогательные методы ---
    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    private static double Lerp(double t, double a, double b) => a + t * (b - a);

    // Grad для 2D
    private static double Grad(int hash, double x, double y)
    {
        int h = hash & 15;
        double u = h < 8 ? x : y;
        double v = h < 4 ? y : h == 12 || h == 14 ? x : 0;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }

    // Grad для 3D
    private static double Grad(int hash, double x, double y, double z)
    {
        int h = hash & 15;
        double u = h < 8 ? x : y;
        double v = h < 4 ? y : h == 12 || h == 14 ? x : z;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
}