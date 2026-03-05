// --- START OF FILE DebugDraw.cs ---
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

public struct PersistentLine
{
    public Vector3 Start, End, Color;
    public float TimeLeft;
}

public static class DebugDraw
{
    private static readonly List<PersistentLine> _lines = new();
    private static readonly object _lock = new object();

    public static void AddLine(Vector3 start, Vector3 end, Vector3 color, float duration = 3.0f)
    {
        lock (_lock) _lines.Add(new PersistentLine { Start = start, End = end, Color = color, TimeLeft = duration });
    }

    public static void AddSphere(Vector3 center, float radius, Vector3 color, float duration = 3.0f)
    {
        int segments = 16;
        lock (_lock)
        {
            for (int i = 0; i < segments; i++)
            {
                float a1 = (i / (float)segments) * MathHelper.TwoPi;
                float a2 = ((i + 1) / (float)segments) * MathHelper.TwoPi;

                // Плоскость XZ (горизонтальный круг)
                _lines.Add(new PersistentLine { Start = center + new Vector3((float)Math.Cos(a1) * radius, 0, (float)Math.Sin(a1) * radius), End = center + new Vector3((float)Math.Cos(a2) * radius, 0, (float)Math.Sin(a2) * radius), Color = color, TimeLeft = duration });
                // Плоскость XY (вертикальный круг)
                _lines.Add(new PersistentLine { Start = center + new Vector3((float)Math.Cos(a1) * radius, (float)Math.Sin(a1) * radius, 0), End = center + new Vector3((float)Math.Cos(a2) * radius, (float)Math.Sin(a2) * radius, 0), Color = color, TimeLeft = duration });
                // Плоскость YZ (вертикальный круг 2)
                _lines.Add(new PersistentLine { Start = center + new Vector3(0, (float)Math.Cos(a1) * radius, (float)Math.Sin(a1) * radius), End = center + new Vector3(0, (float)Math.Cos(a2) * radius, (float)Math.Sin(a2) * radius), Color = color, TimeLeft = duration });
            }
        }
    }

    public static void UpdateAndRender(float dt, LineRenderer lineRenderer)
    {
        lock (_lock)
        {
            for (int i = _lines.Count - 1; i >= 0; i--)
            {
                var line = _lines[i];
                line.TimeLeft -= dt;
                
                if (line.TimeLeft <= 0)
                {
                    _lines.RemoveAt(i);
                }
                else
                {
                    lineRenderer.DrawLine(line.Start, line.End, line.Color);
                    _lines[i] = line;
                }
            }
        }
    }
}