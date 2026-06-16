namespace PSR.Service.Api.Data.Entities;

public class Part : ITimestamps
{
    public long Id { get; set; }
    public string ItemCode { get; set; } = string.Empty;   // legacy PSCode / ItemCode (unique)
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }                   // legacy "Group"
    public string? Unit { get; set; }

    public decimal PurchaseRate { get; set; }
    public decimal DealerRate { get; set; }
    public decimal CustomerRate { get; set; }
    public string? HsnCode { get; set; }
    public decimal GstPercent { get; set; }

    public bool IsSerialTracked { get; set; }
    public string? Remarks { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
