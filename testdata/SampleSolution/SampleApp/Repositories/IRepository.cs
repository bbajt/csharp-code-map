namespace SampleApp.Repositories;

using SampleApp.Models;

/// <summary>Generic repository for persistent storage of entities.</summary>
public interface IRepository<T> where T : IEntity
{
    Task<T?> FindByIdAsync(int id, CancellationToken ct = default);
    Task SaveAsync(T entity, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default);
}
