using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.SpareSales;

/// <summary>One item being sold. <paramref name="UnitRate"/> is tax-EXCLUSIVE and optional — left null the
/// server prices it from the parts master using <paramref name="RateType"/> (which itself defaults to the
/// dealer/customer column implied by the party). Supplying a rate records the line as a Manual price.
///
/// <paramref name="Serials"/> names the exact units, and is required for a serial-tracked part: one
/// serial per unit, so a line for 5 carries 5 of them. A part sold by quantity must send none. Nothing
/// is tracked by saving them — they are claims until the sale is marked sold, which is where they
/// become units at the customer. See <see cref="Data.Entities.SpareSaleLineSerial"/>.</summary>
public record SpareSaleLineRequest(
    [Required] long PartId,
    [Range(1, 1_000_000)] int Qty,
    [StringLength(300)] string? Description,
    [StringLength(20)] string? RateType,
    [Range(0, 10_000_000)] decimal? UnitRate,
    List<string>? Serials = null);

/// <summary>Create or replace a spare sale. The party is EITHER a dealer (CustomerType=Dealer + DealerId)
/// or a direct customer (CustomerType=Direct + CustomerId or a typed CustomerName, match-or-created).
///
/// <paramref name="Address"/>, <paramref name="PartyState"/> and <paramref name="PartyStateCode"/> are the
/// party's billing details as the entry form showed them — prefilled from the master, editable, and kept
/// on the sale. A state is required: its GST code decides whether the documents raised against this sale
/// carry CGST + SGST or IGST, and it is also what the register's statewise filter reads.</summary>
public record SaveSpareSaleRequest(
    [Required, StringLength(20)] string CustomerType,
    long? DealerId,
    long? CustomerId,
    [StringLength(200)] string? CustomerName,
    [StringLength(30)] string? Phone,
    [StringLength(500)] string? Address,
    DateTime? SaleDate,
    [StringLength(500)] string? Remarks,
    [Required, MinLength(1)] List<SpareSaleLineRequest> Lines,
    [StringLength(60)] string? PartyState = null,
    [StringLength(10)] string? PartyStateCode = null);

public record SalePaymentRequest([Required, StringLength(20)] string Status);

/// <summary>Remember what the generate form was told about the courier, so the next PI for this sale
/// opens with it already filled in. Saved when the form is completed, whether or not the document that
/// followed was kept — which is the point: a preview that was looked at and discarded used to take the
/// typed courier details with it. Both fields are optional; blanks clear what was remembered.</summary>
public record SaveSaleCourierRequest(
    [StringLength(80)] string? CourierMode,
    [Range(0, 10_000_000)] decimal? CourierCharges);

/// <summary>Money fields are null for roles that may see the sale but not its pricing (store_manager).
///
/// The stock figures are live, not snapshots. <paramref name="WarehouseOnHand"/> is the physical balance,
/// and the only stock figure a sale deals in. <paramref name="ReturnedQty"/> is how many of this line have
/// since come back.
///
/// <paramref name="Available"/> no longer means anything separate — it is sent equal to
/// <paramref name="WarehouseOnHand"/>. It used to be on-hand minus what other unsold sales had claimed;
/// that idea is gone (see SpareSaleService.ApplyAsync), and the field is kept on the wire only so a client
/// still on the previous build reads the right number instead of a zero.</summary>
/// <param name="IsSerialTracked">Whether this part is sold unit by unit. Carried on the line so the
/// register can say that the serials below it are the whole of what this line is for, rather than
/// leaving an empty list to read as "none entered yet" on a part that never has any.</param>
/// <param name="Serials">The units named on this line, in entry order. Claims while the sale is
/// unsold; the units themselves once it is marked sold.</param>
public record SpareSaleLineDto(
    long Id, long PartId, string ItemCode, string Description, string? HsnCode, string? Unit,
    int Qty, string RateType, decimal? UnitRate, decimal GstPercent,
    decimal? TaxableAmount, decimal? TaxAmount, decimal? LineTotal,
    int WarehouseOnHand, int Available, int ReturnedQty,
    bool IsSerialTracked = false, List<string>? Serials = null);

/// <summary>What the warehouse holds for one part, asked per row as the sale form is typed.
///
/// <paramref name="Committed"/> is always 0 and <paramref name="Available"/> always equals
/// <paramref name="OnHand"/>. Both are kept on the wire for one release so that a client on the previous
/// build — which reads Available and subtracts Committed to label the row — still shows the on-hand
/// figure rather than reading absent fields as zero and refusing every line.</summary>
public record PartAvailabilityDto(long PartId, int OnHand, int Committed, int Available);

// ----- serials on a sale -----

/// <summary>One part's worth of serials, asked about before the sale is saved.</summary>
public record SaleSerialCheckLine([Required] long PartId, [Required] List<string> Serials);

/// <summary>Ask what is wrong with the serials typed on a sale form, before saving it.
///
/// A sale form can be refused for a serial in two quite different ways, and the counter has to be
/// told which: a unit the shop already tracks is simply not for sale here and there is nothing to
/// decide, while a unit another UNSOLD sale has claimed is a question — which of the two sales keeps
/// it. Finding that out from the 400 that <c>POST /spare-sales</c> returns is too late and too thin:
/// the form has already been submitted, and a sentence cannot say which sale to release.
///
/// <paramref name="SaleId"/> is the sale being edited, whose own claims are not conflicts with
/// itself. Null when entering a new one.</summary>
public record SaleSerialCheckRequest(
    long? SaleId,
    [Required, MinLength(1)] List<SaleSerialCheckLine> Lines);

/// <summary>Why one serial cannot go on this sale.
///
/// <paramref name="Releasable"/> is the whole point of the shape. True means another unsold sale holds
/// the claim and the counter may take it off that sale (<c>POST /spare-sales/serials/release</c>);
/// false means the unit is already in the serial ledger — issued, deployed, on the shelf, scrapped —
/// and there is no way through. <paramref name="HeldBySaleNo"/> is set only in the first case, because
/// only then is there a sale to name.</summary>
public record SaleSerialConflictDto(
    long PartId, string ItemCode, string SerialNumber, string Reason,
    bool Releasable, long? HeldBySaleId = null, string? HeldBySaleNo = null);

public record SaleSerialCheckResult(List<SaleSerialConflictDto> Conflicts);

/// <summary>Take these serials off whichever unsold sale is holding them, so the sale being written
/// now can have them. One call however many sales are involved — the counter answered one question,
/// not one per sale.</summary>
public record ReleaseSaleSerialsRequest([Required, MinLength(1)] List<SaleSerialRef> Serials);

public record SaleSerialRef([Required] long PartId, [Required, StringLength(128)] string SerialNumber);

/// <summary>What was actually let go, and off which sales — so the counter is told that the other
/// sale is now a unit short rather than finding out when it refuses to be marked sold.</summary>
public record ReleaseSaleSerialsResult(int Released, List<string> AffectedSaleNos);

// ----- returns -----
public record SaleReturnLineRequest([Required] long PartId, [Range(1, 1_000_000)] int Qty);

/// <summary>Record goods coming back from an invoiced sale. A reason is required: this moves warehouse
/// stock, and a movement nobody can explain later is the thing a stock audit trips over.</summary>
public record CreateSaleReturnRequest(
    DateTime? ReturnDate,
    [Required, StringLength(500)] string Reason,
    [Required, MinLength(1)] List<SaleReturnLineRequest> Lines);

public record SaleReturnLineDto(long PartId, string ItemCode, int Qty);

public record SaleReturnDto(
    long Id, string ReturnNo, DateTime ReturnDate, string Reason,
    string? CreatedByUsername, DateTime CreatedAt, List<SaleReturnLineDto> Lines);

/// <summary><paramref name="SoldAt"/> is the stock axis and the only one of these that says whether the
/// goods have left the warehouse — status, payment and the document numbers say nothing about it.</summary>
public record SpareSaleListItemDto(
    long Id, string SaleNo, DateTime SaleDate, string CustomerType, string PartyName,
    string Status, string PaymentStatus, string? PiNo, string? InvNo,
    int LineCount, decimal? TotalAmount, DateTime? SoldAt);

public record SpareSaleDetailDto(
    long Id, string SaleNo, DateTime SaleDate, string CustomerType,
    long? DealerId, long? CustomerId, string PartyName, string? PartyAddress,
    string? PartyGstin, string? PartyState, string? PartyStateCode,
    string Status, string PaymentStatus,
    string? PiNo, DateTime? PiDate, string? InvNo, DateTime? InvDate,
    decimal? TaxableAmount, decimal? TaxAmount, decimal? TotalAmount,
    string? Remarks, string? CreatedByUsername, DateTime CreatedAt,
    List<SpareSaleLineDto> Lines,
    List<SaleReturnDto> Returns,
    // Stock: null until someone marks the sale sold. Nothing else on this record moves the warehouse.
    DateTime? SoldAt = null, string? SoldByUsername = null,
    // Last courier details entered on a generate form for this sale — the next one opens on them.
    string? CourierMode = null, decimal? CourierCharges = null);
