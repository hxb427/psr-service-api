using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Stock;

public record StockRowDto(long PartId, string ItemCode, string Name, string? Unit, int OnHand);

public record ReceiptRequest([Required] long PartId, [Range(1, 1_000_000)] int Qty, [StringLength(500)] string? Remarks);
public record AdjustRequest([Required] long PartId, int Delta, [StringLength(500)] string? Remarks);

public record StockMovementDto(
    long Id, long PartId, string ItemCode, string MovementType, int Quantity,
    long? TechnicianId, string? ReferenceType, long? ReferenceId, string? Remarks, DateTime CreatedAt);

public record CreateStockRequestRequest([Required] long PartId, [Range(1, 1_000_000)] int Qty, [StringLength(500)] string? Remarks);

public record StockRequestDto(
    long Id, string RequestNo, long RequestedByUserId, string? RequestedByUsername,
    DateTime RequestDate, long PartId, string ItemCode, string PartName,
    int QtyRequested, int QtyIssued, string Status, DateTime? IssuedDate, string? Remarks);

public record IssueRequest([Range(1, 1_000_000)] int Qty);

public record TechInventoryRowDto(long PartId, string ItemCode, string Name, string? Unit, int OnHand);

public record CreateStockReturnRequest([Required] long PartId, [Range(1, 1_000_000)] int Qty, [StringLength(500)] string? Remarks);

public record StockReturnDto(
    long Id, string ReturnNo, long TechnicianId, string? TechnicianUsername,
    long PartId, string ItemCode, string PartName, int Qty, string Status,
    DateTime? AcknowledgedDate, string? Remarks, DateTime CreatedAt);
