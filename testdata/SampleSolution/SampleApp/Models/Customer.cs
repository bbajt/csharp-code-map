namespace SampleApp.Models;

/// <summary>Represents a customer entity with contact information.</summary>
public class Customer : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
}
