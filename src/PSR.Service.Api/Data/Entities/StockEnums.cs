namespace PSR.Service.Api.Data.Entities;

public enum MovementType
{
    Receipt,      // warehouse += qty
    Issue,        // warehouse -= qty ; technician += qty
    Return,       // technician -= qty ; warehouse += qty
    Consumption,  // technician -= qty (used in a service; applied in Phase 4)
    Adjustment,   // warehouse += qty (qty may be negative)
}

public enum StockRequestStatus
{
    Pending,
    Partial,
    Issued,
    Cancelled,
}

public enum StockReturnStatus
{
    Pending,
    Stocked,   // acknowledged + added back to warehouse
    Missing,   // rejected — not received
}
