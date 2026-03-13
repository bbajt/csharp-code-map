namespace SampleApp.Models;

/// <summary>Represents an audit log entry recording a domain action.</summary>
public class AuditEntry : AuditableEntity
{
    public string EntityId { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? Changes { get; set; }
}
