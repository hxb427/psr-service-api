using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Documents;

// Generate a PI / Invoice / DC over one OR MORE completed jobs of a single customer (old-app workflow).
// Party tax fields are entered here (default to the jobs' party). Lines optionally override the per-unit
// rate/qty/remarks (managers may edit); jobs not listed in Lines are auto-priced from their service lines.
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
public record DocLineOverride(
    long ServiceId,
    [StringLength(300)] string? Description,
    [StringLength(30)] string? Warranty,
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

public record DocumentLineDto(
    long Id, long? ServiceJobId, string Description, string? Warranty, string? ServiceChallan, string? HsnCode,
    int Qty, decimal UnitRate, decimal TaxableAmount, decimal GstPercent, decimal TaxAmount, decimal LineTotal,
    string? Remarks, long? PartId);

public record DocumentDto(
    long Id, string DocType, string DocNo, DateTime DocDate, List<long> ServiceIds, List<string> ServiceNos,
    string PartyName, string? PartyAddress, string? PartyGstin, string? PartyState, string? PartyStateCode, bool IsInterState,
    decimal TaxableAmount, decimal CgstAmount, decimal SgstAmount, decimal IgstAmount, decimal CourierCharges, decimal TotalAmount,
    string? CourierMode, string? Remarks, DateTime CreatedAt, List<DocumentLineDto> Lines,
    long? SpareSaleId, string? SaleNo);

// Source is "Service" or "Sale" — one register lists both, and JobCount is 0 on a sale document.
public record DocumentListItemDto(
    long Id, string DocType, string DocNo, DateTime DocDate, int JobCount, string PartyName, decimal TotalAmount,
    string Source, string? SaleNo);
