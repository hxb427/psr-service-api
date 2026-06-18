namespace PSR.Service.Api.Data.Entities;

public enum MovementType
{
    Receipt,      // warehouse += qty
    Issue,        // warehouse -= qty ; technician += qty
    Return,       // technician -= qty ; warehouse += qty
    Consumption,  // technician -= qty (parts used while servicing; applied on service complete)
    Replacement,  // warehouse -= qty (a whole replacement unit shipped out for a Replaced service)
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
