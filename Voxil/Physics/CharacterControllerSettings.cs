// /Physics/CharacterControllerSettings.cs
using System.Numerics;

// Перечисление для удобного выбора пресета
public enum ControllerPreset
{
    Normal,
    DebugLevitate
}

// Структура, хранящая ВСЕ настраиваемые параметры
public struct CharacterControllerSettings
{
    // Движение
    public float WalkSpeed;
    public float SprintSpeed;
    public float JumpVelocity;
    public float MovementDamping;
    public float MovementAcceleration;

    // Парение (Hover)
    public float HoverHeight;
    public float SpringFrequency;
    public float SpringDamping;

    // Физика
    public Vector3 Gravity;
}

// Статический класс с готовыми пресетами
public static class ControllerSettingsPresets
{
    public static readonly CharacterControllerSettings Normal = new CharacterControllerSettings
    {
        WalkSpeed = 4.3f,
        SprintSpeed = 5.6f,
        JumpVelocity = 7.0f,
        // --- ИЗМЕНЕНИЯ ЗДЕСЬ ---
        MovementDamping = 0.0001f,      // Очень сильное торможение (близкое к нулю)
        MovementAcceleration = 100f,   // Более плавное, но мощное ускорение

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
        // --- И ИЗМЕНЕНИЯ ЗДЕСЬ ---
        MovementDamping = 0.0001f,
        MovementAcceleration = 80f, // Чуть менее резкое для левитации

        HoverHeight = 0.2f,
        SpringFrequency = 25f, // Было 50f. Уменьшаем жесткость вдвое.
        SpringDamping = 8f,    // Было 15f. Снижаем демпфирование под новую жесткость.
        Gravity = new Vector3(0, -15, 0)
    };
}