namespace PSR.Service.Api.Data.Entities;

// Service job state machine (mirrors the legacy flag-derived flow):
//   Inward → Assigned (technician + priority set, awaiting ack) → InService (technician acknowledged)
//   → Completed (= pending dispatch) → Dispatched | Stocked
// Total-loss branch: InService → (complete with IsTotalLoss) → ReplacementApprovalPending
//   → Replaced (replacement issued) | TotalLoss (left as total loss, no dispatch).
// Acknowledged / PendingDispatch are LEGACY values kept so pre-refactor rows still materialize; the
// current flow does not produce them (acknowledgement is the AckStatus flag + the InService status;
// "pending dispatch" is just the Completed/ReplacementApprovalPending bucket).
public enum ServiceStatus
{
    Inward,
    Assigned,
    InService,
    Completed,
    ReplacementApprovalPending,
    Dispatched,
    Stocked,
    Replaced,
    TotalLoss,
    Acknowledged,      // legacy
    PendingDispatch,   // legacy
}

public enum Priority { Low, Normal, High, Urgent }

public enum AckStatus { Pending, Acknowledged }

public enum PaymentStatus { Pending, Partial, Paid }

public enum WarrantyStatus { Unknown, InWarranty, OutOfWarranty }

public enum ServiceLineType { Component, ServiceCharge, Replacement }
