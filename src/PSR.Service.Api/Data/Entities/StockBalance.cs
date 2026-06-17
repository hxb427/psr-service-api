namespace PSR.Service.Api.Data.Entities;

/// <summary>
/// On-hand cache, reconcilable from the ledger. Keyed by (part_id, technician_id) where
/// technician_id = 0 means the warehouse. Mutated via atomic guarded SQL (see StockLedgerService),
/// so no read-modify-write races.
/// </summary>
public class StockBalance
{
    public const long Warehouse = 0;

    public long Id { get; set; }
    public long PartId { get; set; }
    public long TechnicianId { get; set; }   // 0 = warehouse
    public int OnHand { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
