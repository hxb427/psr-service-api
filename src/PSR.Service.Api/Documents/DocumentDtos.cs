using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Documents;

// Generate a PI / Invoice / DC for a service job. Party tax fields are entered here (like the old app's
// generate form) — they default to the job's party master when omitted. LineIds null/empty = bill all lines.
public record GenerateDocumentRequest(
    [Required] string DocType,
    DateTime? DocDate,
    [StringLength(200)] string? PartyName,
    [StringLength(500)] string? PartyAddress,
    [StringLength(20)] string? PartyGstin,
    [StringLength(60)] string? PartyState,
    [StringLength(10)] string? PartyStateCode,
    [StringLength(80)] string? CourierMode,
    [Range(0, 10_000_000)] decimal? CourierCharges,
    [StringLength(500)] string? Remarks,
    List<long>? LineIds);

public record DocumentLineDto(
    long Id, string Description, string? HsnCode, int Qty, decimal UnitRate,
    decimal TaxableAmount, decimal GstPercent, decimal TaxAmount, decimal LineTotal);

public record DocumentDto(
    long Id, string DocType, string DocNo, DateTime DocDate, long? ServiceId, string? ServiceNo,
    string PartyName, string? PartyAddress, string? PartyGstin, string? PartyState, string? PartyStateCode, bool IsInterState,
    decimal TaxableAmount, decimal CgstAmount, decimal SgstAmount, decimal IgstAmount, decimal CourierCharges, decimal TotalAmount,
    string? CourierMode, string? Remarks, DateTime CreatedAt, List<DocumentLineDto> Lines);

public record DocumentListItemDto(
    long Id, string DocType, string DocNo, DateTime DocDate, long? ServiceId, string? ServiceNo,
    string PartyName, decimal TotalAmount);
