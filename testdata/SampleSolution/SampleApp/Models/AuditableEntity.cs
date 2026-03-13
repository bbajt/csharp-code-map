namespace SampleApp.Models;

/// <summary>Base class for entities that track creation and modification times.</summary>
public abstract class AuditableEntity : IEntity
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; protected set; }
    public DateTime? UpdatedAt { get; protected set; }

    protected AuditableEntity()
    {
        CreatedAt = DateTime.UtcNow;
    }
}
