using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Services;

// ----- customers -----
public record CustomerDto(long Id, string Name, string? OrganizationName, string? Phone, string? Email, string? Address, bool IsActive);

public record CreateCustomerRequest(
    [Required, StringLength(200)] string Name,
    [StringLength(200)] string? OrganizationName,
    [StringLength(50)] string? Phone,
    [StringLength(200)] string? Email,
    [StringLength(500)] string? Address);

// ----- service create (inward) -----
// Either CustomerId (existing) OR CustomerName (match-or-create) must be supplied.
public record CreateServiceRequest(
    long? CustomerId,
    [StringLength(200)] string? CustomerName,
    [StringLength(200)] string? OrganizationName,
    [StringLength(50)] string? Phone,
    [StringLength(200)] string? Email,
    [StringLength(500)] string? Address,
    [Required, StringLength(100)] string SerialNo,
    [StringLength(50)] string? PsCode,
    [StringLength(100)] string? ModelName,
    [StringLength(500)] string? Description,
    [StringLength(1000)] string? ReportedProblem,
    long? DealerId,
    string? WarrantyStatus,
    [StringLength(50)] string? InwardDcNo,
    [StringLength(50)] string? ChallanNo,
    [StringLength(30)] string? CustomerType,
    string? Priority,
    DateTime? DateReceived);

// Multi-item inward: a shared header + one row per unit (each becomes its own service job).
public record InwardBatchRequest(
    long? CustomerId,
    [StringLength(200)] string? CustomerName,
    [StringLength(200)] string? OrganizationName,
    [StringLength(50)] string? Phone,
    [StringLength(500)] string? Address,
    [StringLength(30)] string? CustomerType,
    long? DealerId,
    [StringLength(50)] string? ChallanNo,
    [StringLength(50)] string? InwardDcNo,
    DateTime? DateReceived,
    string? Priority,
    List<InwardItem> Items);

public record InwardItem(
    [Required, StringLength(100)] string SerialNo,
    [StringLength(50)] string? PsCode,
    [StringLength(100)] string? ModelName,
    [StringLength(500)] string? Description,
    [StringLength(1000)] string? ReportedProblem,
    string? WarrantyStatus);

/// <summary>Alerts are advisory, never blocking: a unit seen before, or one last held by a different
/// party. The counter is holding the item while there is still time to check a misread serial.</summary>
public record InwardBatchResult(string? ChallanNo, int Created, List<ServiceListItemDto> Jobs,
    List<string>? UnitAlerts = null);

public record TechnicianOptionDto(long Id, string Username, string? FullName);

// Home dashboard counts (role-scoped: a technician sees only their own jobs / completions).
public record ServiceSummaryDto(
    int Inward, int InService, int ReplacementPending, int PendingDispatch, int Closed,
    int ServicedToday, int ServicedThisWeek, int ServicedThisMonth, int PendingStockRequests);

// Dashboard overview: this-month vs last-month serviced (jobs reaching a terminal stage) + avg turnaround
// (days from DateReceived to that terminal stage). Role-scoped — a technician sees only their own jobs.
public record ServiceOverviewDto(
    string ThisMonthLabel, int ThisMonthServiced, double ThisMonthAvgTatDays,
    string LastMonthLabel, int LastMonthServiced, double LastMonthAvgTatDays);

// ----- transitions -----
public record NoteRequest([StringLength(500)] string? Note);
public record AssignRequest([Required] long TechnicianId, string? Priority, DateTime? PromisedDate);
public record AddLineRequest(
    [Required] string LineType,
    long? PartId,
    long? ServiceChargeId,
    [StringLength(255)] string? Description,
    [Range(1, 1_000_000)] int Qty = 1,
    [StringLength(100)] string? ReplacementSerialNo = null);
public record CompleteRequest([StringLength(1000)] string? TechnicianRemarks);
// Reference number is mandatory at dispatch; the outward DC number is optional.
/// <summary>Every field is optional and every one of them means "overwrite this". Dispatch is normally
/// pressed on a job that already carries its outward reference and DC number — set when the reference
/// was stamped, or when the DC document was generated — so the desk sends an empty body and the job
/// keeps what it has. A caller that does have new values (the field app, capturing a courier docket at
/// hand-over) can still send them.</summary>
/// <summary>Several lines added in one go — the technician picks quantities against their whole holding
/// and the charges list, then saves once. Applied as a unit: a batch that cannot be covered in full adds
/// nothing, rather than leaving the job with the first three of five lines and an error about the
/// fourth.</summary>
public record AddLinesRequest([Required, MinLength(1)] List<AddLineRequest> Lines);

public record DispatchRequest(
    [StringLength(80)] string? ReferenceNo = null,
    [StringLength(50)] string? OutwardDcNo = null,
    DateTime? DcDate = null);
public record ReplaceRequest(
    [Required, StringLength(100)] string ReplacementSerialNo,
    long? ReplacementPartId,
    [Range(1, 1_000_000)] int Qty = 1,
    [StringLength(500)] string? Note = null);
public record PaymentRequest([Required] string Status);
// Set the courier / gate-pass reference without dispatching (legacy "Set Outward Reference").
public record OutwardReferenceRequest(
    [Required, StringLength(80)] string ReferenceNo,
    [StringLength(50)] string? OutwardDcNo);
/// <summary>Correct a booked job's descriptive fields (legacy Global Search "Edit Service Record").
/// Every field is sent on every save, so a blank optional field clears it — the dialog shows the
/// current values, which makes "what I see is what is stored" the only safe reading of a blank box.
/// Nothing here moves the workflow; status, technician, payment and lines are untouched.</summary>
public record UpdateServiceRecordRequest(
    [StringLength(200)] string? CustomerName,
    [StringLength(50)] string? InwardDcNo,
    [Required, StringLength(100)] string SerialNo,
    [StringLength(50)] string? PsCode,
    [StringLength(500)] string? Description,
    [StringLength(100)] string? ModelName,
    [StringLength(1000)] string? ReportedProblem,
    string? WarrantyStatus,
    [StringLength(50)] string? PiNo,
    [StringLength(50)] string? OutwardDcNo,
    [StringLength(50)] string? InvNo);

// Record an invoice number raised outside the app (legacy "Set Invoice No").
public record InvoiceNoRequest(
    [Required, StringLength(50)] string InvNo,
    DateTime? InvDate);

// ----- bulk transitions -----
// One request for a whole selection, replacing the client-side loop that fired one POST per job.
// Each request carries the ids plus whatever its single-job counterpart needs. Ids are capped at
// MaxBulkIds server-side, which matches the list page size — a selection cannot exceed one page.

public record BulkIdsRequest([Required, MinLength(1)] long[] Ids);
public record BulkNoteRequest([Required, MinLength(1)] long[] Ids, [StringLength(500)] string? Note);
public record BulkAssignRequest(
    [Required, MinLength(1)] long[] Ids,
    [Required] long TechnicianId,
    string? Priority,
    DateTime? PromisedDate);
public record BulkPaymentRequest([Required, MinLength(1)] long[] Ids, [Required] string Status);
public record BulkDispatchRequest(
    [Required, MinLength(1)] long[] Ids,
    [StringLength(80)] string? ReferenceNo = null,
    [StringLength(50)] string? OutwardDcNo = null,
    DateTime? DcDate = null);
public record BulkOutwardReferenceRequest(
    [Required, MinLength(1)] long[] Ids,
    [Required, StringLength(80)] string ReferenceNo,
    [StringLength(50)] string? OutwardDcNo);
public record BulkInvoiceNoRequest(
    [Required, MinLength(1)] long[] Ids,
    [Required, StringLength(50)] string InvNo,
    DateTime? InvDate);

/// <summary>Per-job reason a bulk action could not be applied. ServiceNo is blank when the id did not
/// resolve to a job at all, which is the only case where the client has no number to show.</summary>
public record BulkFailureDto(long Id, string ServiceNo, string Error);

/// <summary>What a bulk action actually did. Jobs already in the requested state count as succeeded —
/// every bulk action is idempotent, so replaying one after a lost response reports the truth rather
/// than inventing failures.</summary>
public record BulkActionResultDto(IReadOnlyList<long> Succeeded, IReadOnlyList<BulkFailureDto> Failed);

// ----- read -----
public record ServiceListItemDto(
    long Id, string ServiceNo, string? ChallanNo, string? InwardDcNo, long? CustomerId, long? DealerId, string? CustomerName, string SerialNo, string? PsCode, string? ModelName, string? Description,
    string ServiceStatus, string AckStatus, string PaymentStatus, string Priority, string WarrantyStatus,
    long? TechnicianId, string? TechnicianName, DateTime DateReceived, DateTime? PromisedDate,
    // Document refs drive the gated PI → Invoice → DC chain on the dispatch screen. OutwardReferenceNo
    // is here so a bulk dispatch can tell, without opening each job, which rows carry a number the
    // goods can be traced by — dispatch refuses a job with none of PI / DC / outward reference.
    // A replaced unit now waits in the same pending-dispatch queue as a repaired one, so the row has
    // to say which it is — the counter is handing back a different machine from the one booked in.
    string? PiNo, string? InvNo, string? OutwardDcNo, string? OutwardReferenceNo = null,
    string? ReplacementSerialNo = null,
    // A retained job is the shop's own unit sitting in the service sections. The row has to say so,
    // because it cannot be dispatched or billed and the desk should not have to open it to find out.
    string JobKind = "Customer", long? ParentServiceJobId = null);

// UnitPrice/Amount are null for non-pricing roles (technician/store/etc).
public record ServiceLineDto(
    long Id, string LineType, long? PartId, string? PartCode, string? PartName,
    long? ServiceChargeId, string? ServiceChargeName, string? Description, int Qty,
    decimal? UnitPrice, decimal? Amount, string? ReplacementSerialNo);

public record ServiceHistoryDto(
    long Id, string? FromStatus, string ToStatus, long ChangedByUserId, string? ChangedByUsername,
    string? Note, DateTime ChangedAt);

// Total is null for non-pricing roles.
public record ServiceDetailDto(
    long Id, string ServiceNo, string? ChallanNo, string? CustomerType, long? CustomerId, string? CustomerName, string? CustomerPhone,
    long? DealerId, string? DealerName, string SerialNo, string? PsCode, string? ModelName, string? Description,
    string? ReportedProblem, string WarrantyStatus, string? InwardDcNo, string? OutwardDcNo, string? OutwardReferenceNo, DateTime? DcDate,
    string? PiNo, string? InvNo,
    DateTime DateReceived, DateTime? PromisedDate, long? TechnicianId, string? TechnicianName, string Priority, string AckStatus,
    string ServiceStatus, string PaymentStatus, string? TechnicianRemarks, bool IsTotalLoss,
    string? ReplacementSerialNo, long? ReplacementPartId, string? ReplacementPartName,
    decimal? Total, uint RowVersion, List<ServiceLineDto> Lines, List<ServiceHistoryDto> History,
    // The two halves of a swap. Each is reachable from the other because they are one event, and the
    // question asked at the counter ("where is the machine they left with us?") starts from either.
    string JobKind = "Customer",
    long? ParentServiceJobId = null, string? ParentServiceNo = null,
    long? RetainedServiceJobId = null, string? RetainedServiceNo = null,
    // Whether the swap on THIS job can still be undone. Server-decided: the desktop must not have to
    // re-derive a rule that depends on documents and on the other half's progress.
    bool CanCancelSwap = false);

/// <summary>One replacement in the trail, for the counter's "was this one of ours?" lookup.</summary>
public record ServiceReplacementDto(
    long Id, string Kind, long OriginalServiceJobId, string? OriginalServiceNo,
    long? RetainedServiceJobId, string? RetainedServiceNo,
    string? OutgoingSerialNo, string? OutgoingItemCode,
    string? IncomingSerialNo, string? IncomingItemCode,
    string? PartyName, string? Reason, string? ApprovedByUsername,
    DateTime CreatedAt, DateTime? CancelledAt);

/// <summary>Issue an advance replacement: the customer leaves with a unit off the shelf and their own
/// stays behind.
///
/// A serial and nothing else. A swap is the SAME item going out that came in — the customer brought a
/// thing and is handed another of that thing — so the only fact that is not already on the job is
/// which physical unit they walked out with. The item comes from the job's own PS code at both ends.
/// Offering a part to pick asked the counter to re-answer a question the job had already answered,
/// and every wrong answer was a unit off the wrong shelf.
///
/// A different item is not a swap. That is a total loss and a replacement, which has its own route and
/// its own part picker.</summary>
public record SwapRequest(string ReplacementSerialNo, string? Reason);
