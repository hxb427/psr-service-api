namespace PSR.Service.Api.Data.Entities;

public class User : ITimestamps
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Technician who takes stock into the field. Serial-tracked parts issued to a field
    /// technician must be captured per-serial; in-house technicians stay quantity-only.</summary>
    public bool IsFieldTechnician { get; set; }
    public int TokenVersion { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime? PasswordChangedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<UserRole> UserRoles { get; set; } = new();
}
