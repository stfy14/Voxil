using System.Collections.Generic;

public class EntityManager
{
    private readonly List<IEntity> _entities = new();
    private readonly List<IEntity> _toAdd = new();

    public void Register(IEntity entity)
    {
        // Буферизуем добавление, чтобы не модифицировать список во время Update
        _toAdd.Add(entity);
    }

    public void Update(float dt)
    {
        // Сначала добавляем накопившихся
        if (_toAdd.Count > 0)
        {
            _entities.AddRange(_toAdd);
            _toAdd.Clear();
        }

        // Обновляем и удаляем мёртвых
        for (int i = _entities.Count - 1; i >= 0; i--)
        {
            _entities[i].Update(dt);
            if (_entities[i].IsDead)
                _entities.RemoveAt(i);
        }
    }

    public void Clear()
    {
        _entities.Clear();
        _toAdd.Clear();
    }
}