namespace PSR.Service.Api.Data.Entities;

public class ServiceStatusHistory
{
    public long Id { get; set; }
    public long ServiceId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = string.Empty;
    public long ChangedByUserId { get; set; }
    public string? Note { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
