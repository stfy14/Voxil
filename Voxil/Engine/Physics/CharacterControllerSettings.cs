// /Physics/CharacterControllerSettings.cs
using System.Numerics;

public enum ControllerPreset
{
    Normal,
    DebugLevitate,
    Spectator // <--- Добавили
}

public struct CharacterControllerSettings
{
    public float WalkSpeed;
    public float SprintSpeed;
    public float JumpVelocity;
    public float MovementDamping;
    public float MovementAcceleration;

    public float HoverHeight;
    public float SpringFrequency;
    public float SpringDamping;

    public Vector3 Gravity;
}

public static class ControllerSettingsPresets
{
    public static readonly CharacterControllerSettings Normal = new CharacterControllerSettings
    {
        WalkSpeed = 4.3f,
        SprintSpeed = 5.6f,
        JumpVelocity = 7.0f,
        MovementDamping = 0.0001f, 
        MovementAcceleration = 100f,   

        HoverHeight = 0.05f,
        SpringFrequency = 30f,
        SpringDamping = 10f,
        Gravity = new Vector3(0, -20, 0)
    };

    public static readonly CharacterControllerSettings DebugLevitate = new CharacterControllerSettings
    {
        WalkSpeed = 5f,
        SprintSpeed = 8f,
        JumpVelocity = 8.0f,
        MovementDamping = 0.0001f,
        MovementAcceleration = 80f, 

        HoverHeight = 0.2f,
        SpringFrequency = 25f, 
        SpringDamping = 8f,    
        Gravity = new Vector3(0, -15, 0)
    };

    // --- ДОБАВЛЕНО: Режим полета ---
    public static readonly CharacterControllerSettings Spectator = new CharacterControllerSettings
    {
        WalkSpeed = 30.0f,       // Очень быстро
        SprintSpeed = 60.0f,     // Сверхзвуковая скорость для проверки чанков
        JumpVelocity = 0.0f,     // Не используется
        MovementDamping = 10.0f, // Мгновенная остановка
        MovementAcceleration = 200f,
        
        HoverHeight = 0.0f,
        SpringFrequency = 0f,
        SpringDamping = 0f,
        Gravity = Vector3.Zero   // Гравитация отключена
    };
}