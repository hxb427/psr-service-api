namespace PSR.Service.Api.Data.Entities;

public class Dealer : ITimestamps
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;   // unique
    public int WarrantyMonths { get; set; }
    public string? Remarks { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
