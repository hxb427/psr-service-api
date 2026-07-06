namespace PSR.Service.Api.Data.Entities;

/// <summary>Immutable audit row for every transition on a <see cref="ComponentSerial"/>.
/// Never updated or deleted — the accountability trail for a deployed unit.</summary>
public class SerialStatusHistory
{
    public long Id { get; set; }
    public long ComponentSerialId { get; set; }
    public long PartId { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public string? OldStatus { get; set; }
    public string NewStatus { get; set; } = string.Empty;
    public long ChangedByUserId { get; set; }
    public string? Remarks { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
