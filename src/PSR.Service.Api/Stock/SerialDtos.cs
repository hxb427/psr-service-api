using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Stock;

/// <param name="CurrentServiceNo">The repair job holding this unit, when it came back off a field
/// return and is being serviced. What turns "UNDER_REPAIR" into something someone can act on.</param>
public record ComponentSerialDto(
    long Id, long PartId, string ItemCode, string PartName, string SerialNumber,
    string Status, string OwnerType, string? OwnerRef,
    long? TechnicianId, string? TechnicianName,
    DateTime? LastUpdatedAt, DateTime CreatedAt,
    long? CustomerId = null, long? CurrentServiceJobId = null, string? CurrentServiceNo = null);

public record SerialAuditDto(
    long Id, string? OldStatus, string NewStatus,
    long ChangedByUserId, string? ChangedByUsername, string? Remarks, DateTime ChangedAt);

public record ComponentSerialDetailDto(ComponentSerialDto Serial, IReadOnlyList<SerialAuditDto> Audit);

// Admin manual status change (mark missing/found/defective/repaired…). Remarks are mandatory.
public record ChangeSerialStatusRequest(
    [Required, StringLength(20)] string Status,
    [Required, StringLength(500, MinimumLength = 1)] string Remarks);

// Receive a field-returned serial back at the service center (good → RETURNED_TO_SC, faulty → DEFECTIVE).
public record ReceiveSerialReturnRequest(
    bool Defective,
    [Required, StringLength(500, MinimumLength = 1)] string Remarks);
