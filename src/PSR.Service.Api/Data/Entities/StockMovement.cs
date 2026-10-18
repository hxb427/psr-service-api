namespace PSR.Service.Api.Data.Entities;

/// <summary>
/// Append-only stock ledger. Each row is one fact; balance effects are derived from MovementType:
/// Receipt(+wh), Issue(-wh), IssueReceipt(+tech), Return(-tech,+wh), Consumption(-tech),
/// Adjustment(±wh). LossInTransit and DefectiveOnArrival move no balance — they account for the
/// difference between what was dispatched and what became usable stock.
/// Quantity is a positive magnitude except for Adjustment, where it carries the signed correction.
///
/// <see cref="ShelfQty"/> carves the warehouse half of all of that: it is how much of Quantity came
/// off (or went onto) the SHELF rather than the warehouse, so a row's warehouse effect is
/// Quantity - ShelfQty, signed by its type. The technician side is unaffected: an issue off the
/// shelf credits a technician exactly as one off the warehouse does.
/// </summary>
public class StockMovement
{
    public long Id { get; set; }
    public long PartId { get; set; }
    public MovementType MovementType { get; set; }
    public int Quantity { get; set; }
    public long? TechnicianId { get; set; }     // set for Issue/Return/Consumption
    public string? ReferenceType { get; set; }   // STOCK_REQUEST / STOCK_RETURN / SERVICE / MANUAL / SERIAL_STATUS
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

    /// <summary>How many of <see cref="Quantity"/> came off, or went onto, the SHELF rather than the
    /// warehouse. The warehouse effect of any row is therefore <c>Quantity - ShelfQty</c>.
    ///
    /// The shelf is the service centre's pool of units in hand - machines kept off finished jobs,
    /// unused stock booked back from a technician - and it is counted by UNIT, as whichever
    /// <c>component_serials</c> rows are SHELFED and held by the service centre. It deliberately has
    /// no row in <c>stock_balances</c>: a second quantity for the same units is a second number to
    /// keep in step with the first, and the whole reason the shelf exists as its own pool is that a
    /// serviced machine must not be added to the purchased stock of its PS code.
    ///
    /// So the shelf half of a row moves no quantity balance. What this column is FOR is the
    /// warehouse-shaped reports: the stock ledger's Inward/Outward columns and the consumption report
    /// are defined as warehouse movement, and a unit taken off the shelf never touched the warehouse.
    /// Without it those reports would show an outflow no balance change backs, and the ledger would
    /// stop reconciling against <c>stock_balances</c>.
    ///
    /// A QUANTITY rather than a flag, because one issue can come out of both pools: a store filling
    /// an order for three picks two units off the shelf and types the serial off a new box for the
    /// third. That has to stay ONE movement - it is one consignment, with one acknowledgement and one
    /// set of serial links - so the row says how it was made up rather than which pool it was.
    ///
    /// Backfills to zero, which is correct for every row written before the shelf existed: all of
    /// them moved the warehouse for their whole quantity.</summary>
    public int ShelfQty { get; set; }

    public long PerformedByUserId { get; set; }
    public string? Remarks { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
