public interface IEntity
{
    bool IsDead { get; }
    void Update(float dt);
}