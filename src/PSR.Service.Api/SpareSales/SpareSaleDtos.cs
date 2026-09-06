using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.SpareSales;

/// <summary>One item being sold. <paramref name="UnitRate"/> is tax-EXCLUSIVE and optional — left null the
/// server prices it from the parts master using <paramref name="RateType"/> (which itself defaults to the
/// dealer/customer column implied by the party). Supplying a rate records the line as a Manual price.</summary>
public record SpareSaleLineRequest(
    [Required] long PartId,
    [Range(1, 1_000_000)] int Qty,
    [StringLength(300)] string? Description,
    [StringLength(20)] string? RateType,
    [Range(0, 10_000_000)] decimal? UnitRate);

/// <summary>Create or replace a spare sale. The party is EITHER a dealer (CustomerType=Dealer + DealerId)
/// or a direct customer (CustomerType=Direct + CustomerId or a typed CustomerName, match-or-created).</summary>
public record SaveSpareSaleRequest(
    [Required, StringLength(20)] string CustomerType,
    long? DealerId,
    long? CustomerId,
    [StringLength(200)] string? CustomerName,
    [StringLength(30)] string? Phone,
    [StringLength(500)] string? Address,
    DateTime? SaleDate,
    [StringLength(500)] string? Remarks,
    [Required, MinLength(1)] List<SpareSaleLineRequest> Lines);

public record SalePaymentRequest([Required, StringLength(20)] string Status);

/// <summary>Money fields are null for roles that may see the sale but not its pricing (store_manager).
///
/// The stock figures are live, not snapshots. <paramref name="WarehouseOnHand"/> is the physical balance;
/// <paramref name="Available"/> subtracts what other pending sales have already claimed, and is the number
/// that decides whether this sale can actually be invoiced. <paramref name="ReturnedQty"/> is how many of
/// this line have since come back.</summary>
public record SpareSaleLineDto(
    long Id, long PartId, string ItemCode, string Description, string? HsnCode, string? Unit,
    int Qty, string RateType, decimal? UnitRate, decimal GstPercent,
    decimal? TaxableAmount, decimal? TaxAmount, decimal? LineTotal,
    int WarehouseOnHand, int Available, int ReturnedQty);

/// <summary>A part's warehouse position for the sale form, which asks per row as the user types.</summary>
public record PartAvailabilityDto(long PartId, int OnHand, int Committed, int Available);

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
    DateTime? SoldAt = null, string? SoldByUsername = null);
