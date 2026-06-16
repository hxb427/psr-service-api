namespace PSR.Service.Api.Data.Entities;

public class ServiceCharge : ITimestamps
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;   // legacy "Item"
    public decimal Charge { get; set; }
    public decimal TaxPercent { get; set; }
    public string? Remarks { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
