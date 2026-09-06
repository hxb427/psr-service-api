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

public record InwardBatchResult(string? ChallanNo, int Created, List<ServiceListItemDto> Jobs);

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

// ----- read -----
public record ServiceListItemDto(
    long Id, string ServiceNo, string? ChallanNo, string? InwardDcNo, long? CustomerId, long? DealerId, string? CustomerName, string SerialNo, string? PsCode, string? ModelName, string? Description,
    string ServiceStatus, string AckStatus, string PaymentStatus, string Priority, string WarrantyStatus,
    long? TechnicianId, string? TechnicianName, DateTime DateReceived, DateTime? PromisedDate,
    // Document refs drive the gated PI → Invoice → DC chain on the dispatch screen. OutwardReferenceNo
    // is here so a bulk dispatch can tell, without opening each job, which rows carry a number the
    // goods can be traced by — dispatch refuses a job with none of PI / DC / outward reference.
    string? PiNo, string? InvNo, string? OutwardDcNo, string? OutwardReferenceNo = null);

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
    decimal? Total, uint RowVersion, List<ServiceLineDto> Lines, List<ServiceHistoryDto> History);
