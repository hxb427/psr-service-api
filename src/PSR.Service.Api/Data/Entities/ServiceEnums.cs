namespace PSR.Service.Api.Data.Entities;

// Service job state machine:
//   Inward → Assigned (technician + priority set) → Acknowledged (technician received it)
//   → InService (technician started work) → Completed (= pending dispatch) → Dispatched | Stocked
// Total-loss branch: InService → (complete with IsTotalLoss) → ReplacementApprovalPending
//   → Replaced (replacement issued) | TotalLoss (left as total loss, no dispatch).
// Acknowledge and Start are TWO separate technician steps. PendingDispatch is LEGACY (kept so
// pre-refactor rows still materialize); the Completed/ReplacementApprovalPending bucket is "pending dispatch".
public enum ServiceStatus
{
    Inward,
    Assigned,
    Acknowledged,
    InService,
    Completed,
    ReplacementApprovalPending,
    Dispatched,
    Stocked,
    Replaced,
    TotalLoss,
    PendingDispatch,   // legacy
}

public enum Priority { Low, Normal, High, Urgent }

public enum AckStatus { Pending, Acknowledged }

public enum PaymentStatus { Pending, Partial, Paid }

public enum WarrantyStatus { Unknown, InWarranty, OutOfWarranty }

public enum ServiceLineType { Component, ServiceCharge, Replacement }
