using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Documents;

// Generate a PI / Invoice / DC over one OR MORE completed jobs of a single customer (old-app workflow).
// Party tax fields are entered here (and fall back to the jobs' party master). A state has to be settled
// one way or the other — PartyStateCode, PartyState, a GSTIN that opens with a code, or the party master —
// because it decides the tax heads; nothing is assumed when all four are empty. Lines optionally override
// the per-unit rate/qty/remarks (managers may edit); jobs not listed in Lines are auto-priced from their
// service lines, but every job still needs a line carrying its HSN/SAC.
public record GenerateDocumentRequest(
    [Required] string DocType,
    [Required, MinLength(1)] List<long> ServiceIds,
    DateTime? DocDate,
    [StringLength(200)] string? PartyName,
    [StringLength(500)] string? PartyAddress,
    [StringLength(500)] string? ConsigneeAddress,
    [StringLength(20)] string? PartyGstin,
    [StringLength(60)] string? PartyState,
    [StringLength(10)] string? PartyStateCode,
    [StringLength(80)] string? CourierMode,
    [Range(0, 10_000_000)] decimal? CourierCharges,
    [StringLength(500)] string? Remarks,
    List<DocLineOverride>? Lines);

// Per-unit override (manager edit). Rate is tax-INCLUSIVE (matches the old "Rate (incl. tax)" field).
// HsnCode and Unit are typed on the form rather than derived: a serviced unit is not a catalogue row, so
// there is nothing on the job to read a unit off. HsnCode is the one the FORM shows, which is the
// catalogue's code for the unit unless somebody typed over it; the server falls back to the catalogue
// itself, and refuses the line when neither has one (see BillingService.RequireLineHsn).
public record DocLineOverride(
    long ServiceId,
    [StringLength(300)] string? Description,
    [StringLength(30)] string? Warranty,
    [StringLength(20)] string? HsnCode,
    [StringLength(20)] string? Unit,
    decimal? Rate,
    int? Qty,
    [StringLength(300)] string? Remarks);

// Generate the PI or the tax invoice for a direct spare sale. The lines come from the sale itself (they were
// priced when it was entered), so only the party/courier/remarks block is filled in here.
public record GenerateSaleDocumentRequest(
    [Required] string DocType,
    [Required] long SaleId,
    DateTime? DocDate,
    [StringLength(200)] string? PartyName,
    [StringLength(500)] string? PartyAddress,
    [StringLength(500)] string? ConsigneeAddress,
    [StringLength(20)] string? PartyGstin,
    [StringLength(60)] string? PartyState,
    [StringLength(10)] string? PartyStateCode,
    [StringLength(80)] string? CourierMode,
    [Range(0, 10_000_000)] decimal? CourierCharges,
    [StringLength(500)] string? Remarks);

/// <summary>One item on a parts delivery challan. <paramref name="UnitRate"/> is tax-EXCLUSIVE — the
/// convention every goods line prints under — and optional: left null the server reads it off the parts
/// master, taking the dealer column when the challan goes to a dealer and the customer column otherwise.
/// A rate that IS supplied is used as it stands, because a dispatch note declares a value rather than
/// billing one and the declared figure is the person's to set.
///
/// HsnCode and Unit are the same: what the form shows, which is the catalogue's own, unless somebody
/// typed over it.</summary>
public record PartsChallanLineRequest(
    [Required] long PartId,
    [Range(1, 1_000_000)] int Qty,
    [StringLength(300)] string? Description,
    [StringLength(20)] string? HsnCode,
    [StringLength(20)] string? Unit,
    [Range(0, 10_000_000)] decimal? UnitRate);

/// <summary>Raise a delivery challan for parts going out as paperwork only — spares sent to a dealer or
/// to a field technician, with the items typed on the form rather than read off a job or a sale.
///
/// The party is EITHER a dealer (<paramref name="DealerId"/>, which carries its own billing block) or a
/// technician (<paramref name="TechnicianUserId"/>, who has no party master, so the name comes off the
/// user record and the address is typed). Neither is required — a name on its own raises a challan for
/// somebody who is in no list — but both together is refused: a challan goes to one party.
///
/// A state still has to be settled, by code, name, a GSTIN that opens with one, or the dealer master:
/// the challan carries GST, and the code is what decides whether that is CGST + SGST or IGST.
///
/// Nothing on this request touches stock. It writes the document and its lines, and that is all — no
/// ledger movement, no request row, no stamp on any job or sale.</summary>
public record GeneratePartsChallanRequest(
    DateTime? DocDate,
    long? DealerId,
    long? TechnicianUserId,
    [StringLength(200)] string? PartyName,
    [StringLength(500)] string? PartyAddress,
    [StringLength(500)] string? ConsigneeAddress,
    [StringLength(20)] string? PartyGstin,
    [StringLength(60)] string? PartyState,
    [StringLength(10)] string? PartyStateCode,
    [StringLength(80)] string? CourierMode,
    [Range(0, 10_000_000)] decimal? CourierCharges,
    [StringLength(500)] string? Remarks,
    [Required, MinLength(1)] List<PartsChallanLineRequest> Lines);

// UnitRate is tax-INCLUSIVE on a service line (the old app's "Rate (incl. tax)") and tax-EXCLUSIVE on a
// spare-sale line, which is the convention each kind of document prints. SerialNo is the serviced unit's
// serial and is null on spare-sale lines.
public record DocumentLineDto(
    long Id, long? ServiceJobId, string Description, string? Warranty, string? ServiceChallan, string? HsnCode,
    int Qty, decimal UnitRate, decimal TaxableAmount, decimal GstPercent, decimal TaxAmount, decimal LineTotal,
    string? Remarks, long? PartId, string? SerialNo = null, string? Unit = null);

// What the selected jobs will be billed if nobody edits anything — asked for by the generate form so the
// rates are on screen before the document exists. UnitRate is tax-inclusive per unit, at quantity 1.
public record DocumentQuoteRequest([Required, MinLength(1)] List<long> ServiceIds);

// HsnCode is the catalogue's code for the unit, looked up on its PS code, so the form opens with the
// column filled instead of asking for something the price list already knows. Null when the job has no PS
// code or the item it names carries no HSN — the one case somebody has to type it. Last and optional, so a
// client built before it existed still reads this payload.
public record DocumentQuoteLineDto(
    long ServiceId, string ServiceNo, string SerialNo, string Description, string Warranty,
    decimal GstPercent, decimal UnitRate, decimal TaxableAmount, decimal TaxAmount, string? HsnCode = null);

public record DocumentDto(
    long Id, string DocType, string DocNo, DateTime DocDate, List<long> ServiceIds, List<string> ServiceNos,
    string PartyName, string? PartyAddress, string? PartyGstin, string? PartyState, string? PartyStateCode, bool IsInterState,
    decimal TaxableAmount, decimal CgstAmount, decimal SgstAmount, decimal IgstAmount, decimal CourierCharges, decimal TotalAmount,
    string? CourierMode, string? CourierHsnCode, string? Remarks, DateTime CreatedAt, List<DocumentLineDto> Lines,
    long? SpareSaleId, string? SaleNo);

// Source is "Service", "Sale" or "Parts" (a parts delivery challan, which bills neither) — one register
// lists all three, and JobCount is 0 on anything but a service document.
public record DocumentListItemDto(
    long Id, string DocType, string DocNo, DateTime DocDate, int JobCount, string PartyName, decimal TotalAmount,
    string Source, string? SaleNo);
