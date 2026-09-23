using System.ComponentModel.DataAnnotations;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

/// <summary>A warehouse row. <paramref name="InTransitOut"/> and <paramref name="InTransitIn"/> are
/// stock that has physically left one side and not yet been acknowledged by the other, so it sits on
/// NOBODY's balance — the honest position for something in a van. Until these existed the desk could
/// see a shelf count of two and no way to learn that four more were on their way.</summary>
public record StockRowDto(long PartId, string ItemCode, string Name, string? Unit, int OnHand,
    int InTransitOut = 0, int InTransitIn = 0);

/// <summary>Everything currently on nobody's balance, across the whole warehouse. Part counts come
/// with the quantities because "18 units" and "18 units across 14 different items" are different
/// situations and only one of them is a delivery.</summary>
public record InTransitSummaryDto(int OutQty, int OutParts, int InQty, int InParts);

public record ReceiptRequest([Required] long PartId, [Range(1, 1_000_000)] int Qty, [StringLength(500)] string? Remarks,
    [StringLength(50)] string? InvoiceNo = null, [StringLength(100)] string? Source = null);

/// <summary>A whole delivery booked in at once. A supplier's invoice lists a dozen parts and used to
/// mean a dozen openings of the Receive dialog and a dozen round trips, each one re-typing the same
/// invoice number; the numbers that describe the delivery therefore sit on the request, not the line.
/// The lot is one transaction, so a bad line stops the delivery instead of leaving eight parts booked
/// in and an error about the ninth.
///
/// Separate from <see cref="ReceiptRequest"/> rather than folded into it: that endpoint answers with a
/// single stock row, and a build in the field would choke on an array coming back where it expects an
/// object. The one-part route stays exactly as it was.</summary>
public record ReceiptBatchRequest(
    [Required] [MinLength(1)] List<ReceiptLine> Lines,
    [StringLength(500)] string? Remarks = null,
    [StringLength(50)] string? InvoiceNo = null,
    [StringLength(100)] string? Source = null);

/// <summary>One part on a delivery. Quantity only — what the parts cost and who sent them is the same
/// for every line on one invoice.</summary>
public record ReceiptLine([Required] long PartId, [Range(1, 1_000_000)] int Qty);
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
/// register stays one list and nothing has to special-case a movement with no request — one request
/// row per part, because a request is about one part. The whole issue is one transaction: a technician
/// collecting six things at the counter either gets all six or none, rather than four movements and an
/// error about the fifth.
/// Serial rules are the same as issuing against a request.</summary>
public record DirectIssueRequest(
    [Required] long TechnicianId,
    List<DirectIssueLine>? Lines = null,
    [StringLength(500)] string? Remarks = null,
    [StringLength(80)] string? Courier = null, [StringLength(80)] string? TrackingNo = null,
    // ---- the one-part shape, still accepted from desktop builds older than the multi-item issue ----
    // A released client cannot be asked to update before its next issue of stock, and the store is
    // open in the meantime. Dropped once no build in the field sends it.
    long? PartId = null, [Range(1, 1_000_000)] int? Qty = null, IReadOnlyList<string>? Serials = null)
{
    /// <summary>What this issue covers, whichever shape the caller sent. A body carrying Lines is a
    /// current client and its one-part fields are ignored; anything else is read as the single line an
    /// older build meant.</summary>
    public List<DirectIssueLine> EffectiveLines() =>
        Lines is { Count: > 0 } ? Lines
        : PartId is { } pid ? new List<DirectIssueLine> { new(pid, Qty ?? 0, Serials) }
        : new List<DirectIssueLine>();
}

/// <summary>One part on a direct issue. Serials are required (count == Qty) only when a serial-tracked
/// part goes to a field technician, and they are per line because that rule is decided part by part —
/// a handful of boards and a reel of cable can leave the counter together with serials on the boards
/// alone.</summary>
public record DirectIssueLine(
    [Required] long PartId, [Range(1, 1_000_000)] int Qty, IReadOnlyList<string>? Serials = null);

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
/// <param name="Kind">GoodStock (default) or Faulty. Defaulted rather than required so the desktop
/// in-house return, and any client built before faulty returns existed, keeps behaving as it did.</param>
public record CreateStockReturnRequest(
    [Required] long PartId, [Range(1, 1_000_000)] int Qty, [StringLength(500)] string? Remarks,
    [StringLength(80)] string? Courier = null, [StringLength(80)] string? TrackingNo = null,
    List<long>? SerialIds = null, string? Kind = null);

public record StockReturnDto(
    long Id, string ReturnNo, long TechnicianId, string? TechnicianUsername,
    long PartId, string ItemCode, string PartName, int Qty, string Status,
    DateTime? AcknowledgedDate, string? Remarks, DateTime CreatedAt,
    string? Courier = null, string? TrackingNo = null,
    List<StockReturnSerialDto>? Serials = null,
    string Kind = nameof(StockReturnKind.GoodStock),
    List<string>? CreatedServiceNos = null);
