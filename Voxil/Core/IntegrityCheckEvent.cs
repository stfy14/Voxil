using OpenTK.Mathematics;

public readonly struct IntegrityCheckEvent
{
    public readonly Vector3i GlobalPosition;
    public IntegrityCheckEvent(Vector3i pos) => GlobalPosition = pos;
}