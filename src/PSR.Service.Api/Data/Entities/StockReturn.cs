namespace PSR.Service.Api.Data.Entities;

public class StockReturn : ITimestamps
{
    public long Id { get; set; }
    public string ReturnNo { get; set; } = string.Empty;   // unique
    public long TechnicianId { get; set; }
    public long PartId { get; set; }
    public int Qty { get; set; }
    public StockReturnStatus Status { get; set; } = StockReturnStatus.Pending;
    public long? AcknowledgedByUserId { get; set; }
    public DateTime? AcknowledgedDate { get; set; }
    public string? Remarks { get; set; }

    // Field-technician shipment details (legacy technician_return_dispatches).
    public string? Courier { get; set; }
    public string? TrackingNo { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
