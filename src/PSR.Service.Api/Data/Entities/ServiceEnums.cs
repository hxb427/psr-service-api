namespace PSR.Service.Api.Data.Entities;

// Service job state machine:
//   Inward → Assigned (technician + priority set) → Acknowledged (technician received it)
//   → InService (technician started work) → Completed (= pending dispatch) → Dispatched | Stocked
// Total-loss branch: InService → (complete with IsTotalLoss) → ReplacementApprovalPending
//   → Completed (replacement issued — rejoins the normal pending-dispatch queue, and is billed and
//     dispatched from there like any other finished job) | TotalLoss (written off, no dispatch).
// Replaced is RETIRED as a destination: it used to be where issuing a replacement ended, which closed
// the job before it could be billed or handed over. Rows closed that way still carry it, so it stays
// in the enum and in the closed-section / report queries.
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

/// <summary>What a service job IS, as opposed to where it is in the workflow. Defaults to Customer,
/// which is what every job written before this existed was.
///
/// It exists because two kinds of job now sit in the services table holding a unit the SERVICE CENTRE
/// owns rather than a customer's machine, and both of them must be kept out of the routes that assume
/// otherwise — dispatch (there is no customer to dispatch to) and billing (there is nobody to bill).
/// Keeping them apart by status was never possible: they run the same Inward → … → Completed states
/// as any other job, deliberately, because the bench work on them is identical.</summary>
public enum JobKind
{
    /// <summary>An ordinary job on a customer's or dealer's machine.</summary>
    Customer,

    /// <summary>Raised automatically by acknowledging a faulty field return. Backfilled onto every
    /// job carrying a SourceComponentSerialId, which is exactly the set.</summary>
    FieldReturn,

    /// <summary>Raised by an advance replacement: the customer took a unit off the shelf and left
    /// theirs behind, so this job carries a unit the shop now owns. Ends Stocked or TotalLoss.</summary>
    SwapRetained,
}

/// <summary>Why a replacement was issued. Both kinds hand the customer a unit out of warehouse stock;
/// they differ in what happens to the unit that came in.</summary>
public enum ReplacementKind
{
    /// <summary>The incoming unit was written off. No retained job; the unit is scrapped.</summary>
    TotalLoss,

    /// <summary>The incoming unit was kept and a retained job opened on it. The customer is served on
    /// day one and the shop repairs the unit at its own pace, for its own shelf.</summary>
    AdvanceSwap,
}
