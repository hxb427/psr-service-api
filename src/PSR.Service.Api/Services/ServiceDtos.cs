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
public record DispatchRequest([Required, StringLength(50)] string OutwardDcNo, DateTime? DcDate);
public record ReplaceRequest(
    [Required, StringLength(100)] string ReplacementSerialNo,
    long? ReplacementPartId,
    [Range(1, 1_000_000)] int Qty = 1,
    [StringLength(500)] string? Note = null);
public record PaymentRequest([Required] string Status);

// ----- read -----
public record ServiceListItemDto(
    long Id, string ServiceNo, string? ChallanNo, string? InwardDcNo, long? CustomerId, string? CustomerName, string SerialNo, string? PsCode, string? ModelName, string? Description,
    string ServiceStatus, string AckStatus, string PaymentStatus, string Priority, string WarrantyStatus,
    long? TechnicianId, string? TechnicianName, DateTime DateReceived, DateTime? PromisedDate);

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
    string? ReportedProblem, string WarrantyStatus, string? InwardDcNo, string? OutwardDcNo, DateTime? DcDate,
    DateTime DateReceived, DateTime? PromisedDate, long? TechnicianId, string? TechnicianName, string Priority, string AckStatus,
    string ServiceStatus, string PaymentStatus, string? TechnicianRemarks, bool IsTotalLoss,
    string? ReplacementSerialNo, long? ReplacementPartId, string? ReplacementPartName,
    decimal? Total, uint RowVersion, List<ServiceLineDto> Lines, List<ServiceHistoryDto> History);
