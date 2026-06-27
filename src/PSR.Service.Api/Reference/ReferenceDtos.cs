using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Reference;

// Pricing fields are nullable: omitted (null) for non-pricing roles via role-aware projection.
public record PartDto(
    long Id,
    string ItemCode,
    string Name,
    string? Category,
    string? Unit,
    bool IsSerialTracked,
    string? Remarks,
    bool IsActive,
    decimal? PurchaseRate,
    decimal? DealerRate,
    decimal? CustomerRate,
    decimal? GstPercent,
    string? HsnCode);

public record CreatePartRequest(
    [Required, StringLength(50, MinimumLength = 1)] string ItemCode,
    [Required, StringLength(255, MinimumLength = 1)] string Name,
    [StringLength(100)] string? Category,
    [StringLength(20)] string? Unit,
    [Range(0, 99999999)] decimal PurchaseRate,
    [Range(0, 99999999)] decimal DealerRate,
    [Range(0, 99999999)] decimal CustomerRate,
    [StringLength(20)] string? HsnCode,
    [Range(0, 100)] decimal GstPercent,
    bool IsSerialTracked,
    [StringLength(500)] string? Remarks);

public record UpdatePartRequest(
    [Required, StringLength(255, MinimumLength = 1)] string Name,
    [StringLength(100)] string? Category,
    [StringLength(20)] string? Unit,
    [Range(0, 99999999)] decimal PurchaseRate,
    [Range(0, 99999999)] decimal DealerRate,
    [Range(0, 99999999)] decimal CustomerRate,
    [StringLength(20)] string? HsnCode,
    [Range(0, 100)] decimal GstPercent,
    bool IsSerialTracked,
    [StringLength(500)] string? Remarks);

public record ServiceChargeDto(long Id, string Name, decimal Charge, decimal TaxPercent, string? Remarks, bool IsActive);

public record CreateServiceChargeRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Range(0, 99999999)] decimal Charge,
    [Range(0, 100)] decimal TaxPercent,
    [StringLength(500)] string? Remarks);

public record UpdateServiceChargeRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Range(0, 99999999)] decimal Charge,
    [Range(0, 100)] decimal TaxPercent,
    [StringLength(500)] string? Remarks);

public record DealerDto(long Id, string Name, int WarrantyMonths,
    string? Address, string? Gstin, string? State, string? StateCode, string? Remarks, bool IsActive);

public record CreateDealerRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Range(0, 600)] int WarrantyMonths,
    [StringLength(500)] string? Address,
    [StringLength(20)] string? Gstin,
    [StringLength(60)] string? State,
    [StringLength(10)] string? StateCode,
    [StringLength(500)] string? Remarks);

public record UpdateDealerRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Range(0, 600)] int WarrantyMonths,
    [StringLength(500)] string? Address,
    [StringLength(20)] string? Gstin,
    [StringLength(60)] string? State,
    [StringLength(10)] string? StateCode,
    [StringLength(500)] string? Remarks);
