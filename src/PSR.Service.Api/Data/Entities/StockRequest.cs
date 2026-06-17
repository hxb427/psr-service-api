namespace PSR.Service.Api.Data.Entities;

public class StockRequest : ITimestamps
{
    public long Id { get; set; }
    public string RequestNo { get; set; } = string.Empty;   // unique
    public long RequestedByUserId { get; set; }              // technician
    public DateTime RequestDate { get; set; } = DateTime.UtcNow;
    public long PartId { get; set; }
    public int QtyRequested { get; set; }
    public int QtyIssued { get; set; }
    public StockRequestStatus Status { get; set; } = StockRequestStatus.Pending;
    public long? IssuedByUserId { get; set; }
    public DateTime? IssuedDate { get; set; }
    public string? Remarks { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
