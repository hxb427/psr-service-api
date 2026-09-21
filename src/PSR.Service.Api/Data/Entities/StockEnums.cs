namespace PSR.Service.Api.Data.Entities;

public enum MovementType
{
    Receipt,      // warehouse += qty
    Issue,        // warehouse -= qty (dispatched). The technician is credited by IssueReceipt when
                  // they acknowledge it, EXCEPT for a counter handover, which acknowledges itself and
                  // credits in the same breath. Rows written before receipts existed credited the
                  // technician here; StockMovement.CreditedOnIssue says which kind a row is.
    IssueReceipt, // technician += qty actually received and usable (received minus defective)
    LossInTransit,      // qty that left the warehouse and never arrived; credits nobody
    DefectiveOnArrival, // qty that arrived faulty; held by the technician but not usable stock
    ReturnDispatched, // technician -= qty (units physically handed to the courier / the store)
    Return,       // warehouse += qty (the shipment arrived and was acknowledged). Rows written before
                  // dispatch and receipt were split out did both halves at once; StockReturn
                  // .TechnicianDebitedOnShip says which kind a shipment is.
    Consumption,  // technician -= qty (parts used while servicing; applied on service complete)
    ConsumptionReversal, // technician += qty (a completed service was reverted — parts returned to the tech)
    Replacement,  // warehouse -= qty (a whole replacement unit shipped out for a Replaced service)
    Sale,         // warehouse -= qty (spare sold directly to a dealer/customer; applied on Mark as sold)
    SaleReturn,   // warehouse += qty (sold spare sale sent back; applied on the return)
    SaleUnsold,   // warehouse += qty (a sale marked sold in error was un-marked; the exact reversal of Sale)
    Adjustment,   // warehouse += qty (qty may be negative)
    TransferOut,  // sender technician -= qty (units handed over / sent to a peer)
    TransferIn,    // receiver technician += qty received and usable (peer transfer, at acknowledgement)
    Transfer,     // legacy: sender -= qty AND receiver += qty in one row, both applied at acknowledgement
}

public enum StockRequestStatus
{
    Pending,
    Partial,
    Issued,
    Cancelled,
}

/// <summary>What a technician return shipment physically contains. Drives what acknowledgement does,
/// because the two kinds are different journeys that used to share one code path.
///
/// GoodStock is the original behaviour and stays the default, so every row written before this existed
/// (and every client that does not send a kind) keeps acknowledging exactly as it always did.</summary>
public enum StockReturnKind
{
    /// <summary>Unused stock going back: it was issued to the technician, so it is on their balance.
    /// Acknowledging decrements the technician and increments the warehouse.</summary>
    GoodStock,

    /// <summary>Faulty units collected from customers, sent in for service. These were NEVER on the
    /// technician's balance (they came from a customer, not the warehouse), so acknowledging moves no
    /// quantity at all - it flips custody to the service center and opens a repair job per unit. The
    /// quantity only comes back when that job is stocked. Serial-tracked units only, as in the legacy
    /// technician_return_dispatches flow.</summary>
    Faulty,
}

public enum StockReturnStatus
{
    Pending,
    Stocked,   // acknowledged + added back to warehouse
    Missing,   // rejected — not received
}
