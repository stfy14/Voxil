// /Physics/PlayerInputState.cs
using System.Numerics;

/// <summary>
/// Простой класс для хранения состояния ввода игрока,
/// который можно безопасно передать по ссылке в callback-структуру.
/// </summary>
public class PlayerInputState
{
    public Vector2 GoalVelocity;
}