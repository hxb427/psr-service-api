using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Stock;

public record StockRowDto(long PartId, string ItemCode, string Name, string? Unit, int OnHand);

public record ReceiptRequest([Required] long PartId, [Range(1, 1_000_000)] int Qty, [StringLength(500)] string? Remarks,
    [StringLength(50)] string? InvoiceNo = null, [StringLength(100)] string? Source = null);
public record AdjustRequest([Required] long PartId, int Delta, [StringLength(500)] string? Remarks);

public record StockMovementDto(
    long Id, long PartId, string ItemCode, string MovementType, int Quantity,
    long? TechnicianId, string? ReferenceType, long? ReferenceId, string? InvoiceNo, string? Source,
    string? Remarks, DateTime CreatedAt);

public record CreateStockRequestRequest([Required] long PartId, [Range(1, 1_000_000)] int Qty, [StringLength(500)] string? Remarks);

public record StockRequestDto(
    long Id, string RequestNo, long RequestedByUserId, string? RequestedByUsername,
    DateTime RequestDate, long PartId, string ItemCode, string PartName,
    int QtyRequested, int QtyIssued, string Status, DateTime? IssuedDate, string? Remarks,
    string? Courier, string? TrackingNo,
    bool IsSerialTracked, bool RequesterIsFieldTechnician,
    // Who handed the stock over. Null until the first issue; the desktop filters the register by it.
    long? IssuedByUserId = null, string? IssuedByUsername = null);

// Serials is required (count == issued qty) only when a serial-tracked part is issued to a field technician.
public record IssueRequest([Range(1, 1_000_000)] int Qty, [StringLength(80)] string? Courier = null, [StringLength(80)] string? TrackingNo = null,
    IReadOnlyList<string>? Serials = null);

public record TechInventoryRowDto(long PartId, string ItemCode, string Name, string? Unit, int OnHand);

/// <summary>One technician's holding of one part, for the across-the-team view. Carries the holder so
/// the client can group without asking who each id is.</summary>
public record TechnicianStockRowDto(
    long TechnicianId, string TechnicianName, long PartId, string ItemCode, string Name, string? Unit, int OnHand);

// Courier/tracking + serial ids are the field-technician shipment additions (legacy
// technician_return_dispatches); desktop in-house returns send only part/qty/remarks.
public record CreateStockReturnRequest(
    [Required] long PartId, [Range(1, 1_000_000)] int Qty, [StringLength(500)] string? Remarks,
    [StringLength(80)] string? Courier = null, [StringLength(80)] string? TrackingNo = null,
    List<long>? SerialIds = null);

public record StockReturnDto(
    long Id, string ReturnNo, long TechnicianId, string? TechnicianUsername,
    long PartId, string ItemCode, string PartName, int Qty, string Status,
    DateTime? AcknowledgedDate, string? Remarks, DateTime CreatedAt,
    string? Courier = null, string? TrackingNo = null,
    List<StockReturnSerialDto>? Serials = null);
