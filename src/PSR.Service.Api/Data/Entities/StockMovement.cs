namespace PSR.Service.Api.Data.Entities;

/// <summary>
/// Append-only stock ledger. Each row is one fact; balance effects are derived from MovementType:
/// Receipt(+wh), Issue(-wh,+tech), Return(-tech,+wh), Consumption(-tech), Adjustment(±wh).
/// Quantity is a positive magnitude except for Adjustment, where it carries the signed correction.
/// </summary>
public class StockMovement
{
    public long Id { get; set; }
    public long PartId { get; set; }
    public MovementType MovementType { get; set; }
    public int Quantity { get; set; }
    public long? TechnicianId { get; set; }     // set for Issue/Return/Consumption
    public string? ReferenceType { get; set; }   // STOCK_REQUEST / STOCK_RETURN / SERVICE / MANUAL
    public long? ReferenceId { get; set; }
    public string? SerialNo { get; set; }        // reserved for serial tracking (deferred)
    public long PerformedByUserId { get; set; }
    public string? Remarks { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
