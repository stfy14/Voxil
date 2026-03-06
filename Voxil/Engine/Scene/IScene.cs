// --- Engine/Scene/IScene.cs ---

public interface IScene
{
    void OnEnter();
    void OnExit();
    void Update(float deltaTime, InputManager input);
    void Render();
    void OnResize(int width, int height);
}