namespace SampleApp.Repositories;

using SampleApp.Models;

/// <summary>In-memory generic repository implementation.</summary>
public class Repository<T> : IRepository<T> where T : AuditableEntity
{
    private readonly Dictionary<int, T> _store = new();
    private int _nextId = 1;

    public Task<T?> FindByIdAsync(int id, CancellationToken ct = default)
    {
        _store.TryGetValue(id, out T? entity);
        return Task.FromResult(entity);
    }

    public Task SaveAsync(T entity, CancellationToken ct = default)
    {
        if (entity.Id == 0)
            entity.Id = _nextId++;
        _store[entity.Id] = entity;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(int id, CancellationToken ct = default)
    {
        _store.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<T>>(_store.Values.ToList());
    }

    public override string ToString() => $"Repository<{typeof(T).Name}>[{_store.Count} items]";
}
