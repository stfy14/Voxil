// /Physics/PlayerState.cs
using BepuPhysics;
using System.Numerics;

public class PlayerState
{
    public BodyHandle BodyHandle;
    public Vector2 GoalVelocity;
    public bool IsOnGround;
    public float RayT;

    // --- ИЗМЕНЕНИЕ ---
    // Храним текущий набор настроек
    public CharacterControllerSettings Settings;
}