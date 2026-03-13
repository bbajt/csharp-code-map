namespace SampleApp.Models;

/// <summary>Base contract for all domain entities.</summary>
public interface IEntity
{
    /// <summary>Gets the unique identifier for this entity.</summary>
    int Id { get; }
}
