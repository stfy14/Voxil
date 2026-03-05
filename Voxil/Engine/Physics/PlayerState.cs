// /Physics/PlayerState.cs
using BepuPhysics;
using System.Numerics;

public class PlayerState
{
    public BodyHandle BodyHandle;
    public Vector2 GoalVelocity;
    public bool IsOnGround;
    public float RayT;
    
    // --- НОВОЕ ПОЛЕ ---
    public bool IsFlying; // Флаг: если true, физика не вмешивается в скорость

    public CharacterControllerSettings Settings;
}