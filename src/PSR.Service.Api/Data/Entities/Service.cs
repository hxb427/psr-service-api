namespace PSR.Service.Api.Data.Entities;

/// <summary>A service job (legacy service_table), normalized. Line items live in service_lines;
/// status changes are recorded in service_status_history. PI/Invoice/DC numbering is Phase 5.</summary>
public class ServiceJob : ITimestamps
{
    public long Id { get; set; }
    public string ServiceNo { get; set; } = string.Empty;   // auto per-job id (SVCnnnnn), unique
    public string? ChallanNo { get; set; }                  // user-entered service challan, shared across a multi-item inward batch
    public string? CustomerType { get; set; }               // Dealer / Direct toggle (drives which party is set)

    // Party is EITHER a dealer (CustomerType=Dealer) OR a direct customer (CustomerType=Direct).
    public long? CustomerId { get; set; }
    public long? DealerId { get; set; }

    public string SerialNo { get; set; } = string.Empty;
    public string? PsCode { get; set; }                     // item / part code of the serviced unit
    public string? ModelName { get; set; }
    public string? Description { get; set; }
    public string? ReportedProblem { get; set; }
    public WarrantyStatus WarrantyStatus { get; set; } = WarrantyStatus.Unknown;

    public string? InwardDcNo { get; set; }
    public string? OutwardDcNo { get; set; }
    public DateTime? DcDate { get; set; }
    public DateTime DateReceived { get; set; } = DateTime.UtcNow;

    public long? TechnicianId { get; set; }
    public DateTime? PromisedDate { get; set; }             // legacy "priority date" — target turnaround date
    public Priority Priority { get; set; } = Priority.Normal;
    public AckStatus AckStatus { get; set; } = AckStatus.Pending;
    public ServiceStatus ServiceStatus { get; set; } = ServiceStatus.Inward;
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Pending;
    public string? TechnicianRemarks { get; set; }

    public bool IsTotalLoss { get; set; }                   // marked during in-service; routes complete → ReplacementApprovalPending
    public bool IsDeleted { get; set; }                     // soft delete — hidden from lists, never hard-deleted

    // Whole-unit replacement (set when ServiceStatus == Replaced). The incoming/defective unit's serial
    // is SerialNo above; ReplacementSerialNo is the new unit handed to the customer. ReplacementPartId
    // is the catalog part the replacement was drawn from (nullable — only set when it is a stocked part,
    // in which case the warehouse is decremented via a Replacement stock movement).
    public string? ReplacementSerialNo { get; set; }
    public long? ReplacementPartId { get; set; }

    public long CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public uint RowVersion { get; set; }   // optimistic concurrency (bumped on update)

    public List<ServiceLine> Lines { get; set; } = new();
}
