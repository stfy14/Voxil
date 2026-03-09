// --- Game/Player/Player.cs ---
// Изменение: _hotbar[2] = new GlowBallItem() вместо EmptyHandItem
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Collections.Generic;

public class Player
{
    public PlayerController Controller { get; private set; }
    public Camera Camera { get; private set; }
    public float Health { get; set; } = 100f;
    
    private Item[] _hotbar = new Item[9];
    private int _selectedSlot = 0;

    public int SelectedSlot => _selectedSlot;
    public Item CurrentItem => _hotbar[_selectedSlot];

    public Player(PlayerController controller, Camera camera, WorldManager wm)
    {
        Controller = controller;
        Camera = camera;
        
        _hotbar[0] = new EmptyHandItem();
        _hotbar[1] = new DynamiteItem();
        _hotbar[2] = new GlowBallItem();   // ← НОВЫЙ: светящийся шар

        // Остальные слоты — пустая рука
        for (int i = 3; i < 9; i++)
        {
            _hotbar[i] = new EmptyHandItem();
        }
    }

    public void Update(float dt, InputManager input)
    {
        Controller.Update(input, dt);

        if (input.IsKeyPressed(Keys.D1)) _selectedSlot = 0;
        if (input.IsKeyPressed(Keys.D2)) _selectedSlot = 1;
        if (input.IsKeyPressed(Keys.D3)) _selectedSlot = 2;
        if (input.IsKeyPressed(Keys.D4)) _selectedSlot = 3;
        if (input.IsKeyPressed(Keys.D5)) _selectedSlot = 4;
        if (input.IsKeyPressed(Keys.D6)) _selectedSlot = 5;
        if (input.IsKeyPressed(Keys.D7)) _selectedSlot = 6;
        if (input.IsKeyPressed(Keys.D8)) _selectedSlot = 7;
        if (input.IsKeyPressed(Keys.D9)) _selectedSlot = 8;

        if (CurrentItem != null)
        {
            CurrentItem.Update(this, dt);

            if (input.IsMouseButtonPressed(MouseButton.Left))
                CurrentItem.OnUse(this);
        }
    }

    public VoxelObject GetViewModel() => CurrentItem?.ViewModel;
}