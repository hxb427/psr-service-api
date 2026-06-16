namespace PSR.Service.Api.Data.Entities;

// Created now (schema in place) so services can FK to it in Phase 4. No endpoints yet.
public class Customer : ITimestamps
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? OrganizationName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
