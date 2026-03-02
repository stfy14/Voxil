// --- START OF FILE Player.cs ---
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Collections.Generic;

public class Player
{
    public PlayerController Controller { get; private set; }
    public Camera Camera { get; private set; }
    public float Health { get; set; } = 100f;
    
    // ВАЖНО: Массив должен быть проинициализирован здесь!
    private Item[] _hotbar = new Item[9];
    private int _selectedSlot = 0;

    public int SelectedSlot => _selectedSlot;
    public Item CurrentItem => _hotbar[_selectedSlot];

    public Player(PlayerController controller, Camera camera, WorldManager wm)
    {
        Controller = controller;
        Camera = camera;
        
        // Заполняем слоты
        // Если _hotbar был null, здесь вылетала ошибка
        _hotbar[0] = new EmptyHandItem(wm);
        _hotbar[1] = new DynamiteItem(wm);

        // Остальные слоты заполняем пустой рукой, чтобы не было null
        for(int i=2; i<9; i++) 
        {
            _hotbar[i] = new EmptyHandItem(wm);
        }
    }

    public void Update(float dt, InputManager input)
    {
        Controller.Update(input, dt);

        // Переключение слотов (клавиши 1, 2, 3...)
        if (input.IsKeyPressed(Keys.D1)) _selectedSlot = 0;
        if (input.IsKeyPressed(Keys.D2)) _selectedSlot = 1;
        if (input.IsKeyPressed(Keys.D3)) _selectedSlot = 2;
        if (input.IsKeyPressed(Keys.D4)) _selectedSlot = 3;
        if (input.IsKeyPressed(Keys.D5)) _selectedSlot = 4;
        if (input.IsKeyPressed(Keys.D6)) _selectedSlot = 5;
        if (input.IsKeyPressed(Keys.D7)) _selectedSlot = 6;
        if (input.IsKeyPressed(Keys.D8)) _selectedSlot = 7;
        if (input.IsKeyPressed(Keys.D9)) _selectedSlot = 8;
        
        
        // Логика предмета в руках
        if (CurrentItem != null)
        {
            CurrentItem.Update(this, dt);

            if (input.IsMouseButtonPressed(MouseButton.Left))
            {
                CurrentItem.OnUse(this);
            }
        }
    }

    // Возвращает модельку текущего предмета для рендера
    public VoxelObject GetViewModel()
    {
        return CurrentItem?.ViewModel;
    }
}