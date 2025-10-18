// /Physics/PlayerState.cs
using BepuPhysics;
using System.Numerics;

/// <summary>
/// Класс-контейнер для хранения состояния игрока,
/// который можно безопасно передать по ссылке в callback-структуры.
/// </summary>
public class PlayerState
{
    // Будет обновлен после того, как тело игрока создадут
    public BodyHandle BodyHandle;
    // Будет обновляться каждый кадр
    public Vector2 GoalVelocity;
}