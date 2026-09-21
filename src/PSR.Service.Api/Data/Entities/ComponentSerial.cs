namespace PSR.Service.Api.Data.Entities;

/// <summary>
/// One serial-tracked physical unit of a part. Created when a serial-tracked part is dispatched to a
/// field technician; its status/owner is updated as it moves through the field and back. Truth for
/// "where did this deployed unit go". Uniqueness is (part_id, serial_number).
/// </summary>
public class ComponentSerial
{
    public long Id { get; set; }
    public long PartId { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public string? ItemName { get; set; }            // denormalized part name snapshot (display convenience)

    public SerialStatus Status { get; set; } = SerialStatus.Issued;
    public SerialOwnerType OwnerType { get; set; } = SerialOwnerType.ServiceCenter;
    public string? OwnerRef { get; set; }            // human label: technician / customer name, or "In transit to …"
    public long? TechnicianId { get; set; }          // set while assigned to / held by a technician

    /// <summary>Set while the unit is with a customer, when that customer is one of ours. Nullable on
    /// purpose: a field technician may name a walk-in who is not in the customers table, and the unit
    /// still has to be tracked. <see cref="OwnerRef"/> always carries the display name either way.
    /// This is what lets a returned unit open its repair job against the customer it came from.</summary>
    public long? CustomerId { get; set; }

    /// <summary>The service job opened on this unit when it arrived back on a FAULTY return. Set with
    /// UNDER_REPAIR, cleared when the job stocks or scraps the unit. Nullable: units that never went
    /// through the field return loop never have one.</summary>
    public long? CurrentServiceJobId { get; set; }

    public DateTime? LastUpdatedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
