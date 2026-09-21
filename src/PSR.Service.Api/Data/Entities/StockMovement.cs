namespace PSR.Service.Api.Data.Entities;

/// <summary>
/// Append-only stock ledger. Each row is one fact; balance effects are derived from MovementType:
/// Receipt(+wh), Issue(-wh), IssueReceipt(+tech), Return(-tech,+wh), Consumption(-tech),
/// Adjustment(±wh). LossInTransit and DefectiveOnArrival move no balance — they account for the
/// difference between what was dispatched and what became usable stock.
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
    public string? InvoiceNo { get; set; }       // supplier invoice no (receipts)
    public string? Source { get; set; }          // supplier / source (receipts)
    public string? SerialNo { get; set; }        // reserved for serial tracking (deferred)
    /// <summary>True when this Issue credited the technician's balance on the spot rather than
    /// waiting for an acknowledgement — a counter handover, and every row written before receipts
    /// were split out (the column backfills to true, which is what those rows did).
    ///
    /// The acknowledgement handler reads this so an issue that was already credited cannot be
    /// credited a second time when the technician gets round to acknowledging it.</summary>
    public bool CreditedOnIssue { get; set; } = true;

    public long PerformedByUserId { get; set; }
    public string? Remarks { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
