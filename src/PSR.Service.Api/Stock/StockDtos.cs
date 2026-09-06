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

/// <summary>Hand stock to a technician who never raised a request — the counter case, where the
/// technician is standing at the store and the paperwork would only be filled in afterwards. The
/// server still writes a stock request behind it (issued in full, requested by the technician) so the
/// register stays one list and nothing has to special-case a movement with no request.
/// Serial rules are the same as issuing against a request.</summary>
public record DirectIssueRequest(
    [Required] long TechnicianId, [Required] long PartId, [Range(1, 1_000_000)] int Qty,
    [StringLength(500)] string? Remarks = null,
    [StringLength(80)] string? Courier = null, [StringLength(80)] string? TrackingNo = null,
    IReadOnlyList<string>? Serials = null);

/// <summary>A technician stock can be issued to. Role-scoped so the store does not need the admin-only
/// user list to fill a picker.</summary>
public record StockTechnicianDto(long Id, string Username, string? FullName, bool IsFieldTechnician);

/// <param name="IsSerialTracked">Whether fitting this part records a serial. Carried on the holding so
/// the desk can ask for the serial on the rows that need one, instead of showing every row a box that
/// is ignored for most of them.</param>
public record TechInventoryRowDto(
    long PartId, string ItemCode, string Name, string? Unit, int OnHand, bool IsSerialTracked = false);

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
