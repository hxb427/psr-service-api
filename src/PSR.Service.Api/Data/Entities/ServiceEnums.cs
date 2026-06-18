namespace PSR.Service.Api.Data.Entities;

// Service job state machine: Inward → Acknowledged → InService → Completed → PendingDispatch → Dispatched.
// Terminal side-states (alternatives to Dispatched): Stocked (kept in warehouse instead of returned),
// Replaced (the whole unit was swapped for another — see ServiceJob.ReplacementSerialNo).
public enum ServiceStatus
{
    Inward,
    Acknowledged,
    InService,
    Completed,
    PendingDispatch,
    Dispatched,
    Stocked,
    Replaced,
}

public enum Priority { Low, Normal, High, Urgent }

public enum AckStatus { Pending, Acknowledged }

public enum PaymentStatus { Pending, Partial, Paid }

public enum WarrantyStatus { Unknown, InWarranty, OutOfWarranty }

public enum ServiceLineType { Component, ServiceCharge, Replacement }
