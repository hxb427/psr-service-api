namespace PSR.Service.Api.Data.Entities;

public enum TransferStatus
{
    Pending,       // shipped by sender; receiver not yet acknowledged
    Acknowledged,  // receiver acknowledged (per-line/per-serial outcomes recorded)
    Cancelled,     // sender cancelled before acknowledgement — serials rolled back
}

/// <summary>Peer-to-peer stock handoff between technicians (legacy technician_transfers).
/// Balances move only at acknowledgement; serials sit IN_TRANSIT_TECH while pending and
/// ownership stays with the sender so cancel rolls back cleanly.</summary>
public class TechnicianTransfer : ITimestamps
{
    public long Id { get; set; }
    public string TransferNo { get; set; } = string.Empty;   // unique (TRFnnnnn)
    public long FromTechnicianId { get; set; }
    public long ToTechnicianId { get; set; }
    public TransferStatus Status { get; set; } = TransferStatus.Pending;
    public string? Remarks { get; set; }
    public DateTime? AcknowledgedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<TechnicianTransferLine> Lines { get; set; } = new();
}

public class TechnicianTransferLine
{
    public long Id { get; set; }
    public long TransferId { get; set; }
    public long PartId { get; set; }
    public int Qty { get; set; }

    // Receiver's acknowledgement quantities (null until acknowledged).
    public int? QtyReceived { get; set; }
    public int? QtyDefective { get; set; }
    public int? QtyMissing { get; set; }

    public TechnicianTransfer Transfer { get; set; } = null!;
    public List<TechnicianTransferSerial> Serials { get; set; } = new();
}

public class TechnicianTransferSerial
{
    public long Id { get; set; }
    public long TransferLineId { get; set; }
    public long ComponentSerialId { get; set; }
    public SerialAckStatus? AckStatus { get; set; }

    public TechnicianTransferLine Line { get; set; } = null!;
}
