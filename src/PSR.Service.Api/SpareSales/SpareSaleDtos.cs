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
/// <paramref name="WarehouseOnHand"/> is live, not a snapshot — it is what the invoice will draw down.</summary>
public record SpareSaleLineDto(
    long Id, long PartId, string ItemCode, string Description, string? HsnCode, string? Unit,
    int Qty, string RateType, decimal? UnitRate, decimal GstPercent,
    decimal? TaxableAmount, decimal? TaxAmount, decimal? LineTotal, int WarehouseOnHand);

public record SpareSaleListItemDto(
    long Id, string SaleNo, DateTime SaleDate, string CustomerType, string PartyName,
    string Status, string PaymentStatus, string? PiNo, string? InvNo,
    int LineCount, decimal? TotalAmount);

public record SpareSaleDetailDto(
    long Id, string SaleNo, DateTime SaleDate, string CustomerType,
    long? DealerId, long? CustomerId, string PartyName, string? PartyAddress,
    string? PartyGstin, string? PartyState, string? PartyStateCode,
    string Status, string PaymentStatus,
    string? PiNo, DateTime? PiDate, string? InvNo, DateTime? InvDate,
    decimal? TaxableAmount, decimal? TaxAmount, decimal? TotalAmount,
    string? Remarks, string? CreatedByUsername, DateTime CreatedAt,
    List<SpareSaleLineDto> Lines);
