namespace PSR.Service.Api.Data.Entities;

/// <summary>Which serial-tracked units went out on which Issue stock movement. The link the
/// technician's acknowledgement works against (legacy stock_issue_serial_lines).</summary>
public class StockIssueSerial
{
    public long Id { get; set; }
    public long StockMovementId { get; set; }
    public long ComponentSerialId { get; set; }
    /// <summary>Per-serial technician acknowledgement; null until acknowledged.</summary>
    public SerialAckStatus? AckStatus { get; set; }
}

/// <summary>Technician's receipt confirmation for one Issue movement (legacy movement-level ack).
/// Quantities are declarative — no balance mutation; discrepancies are resolved by admin
/// adjustment / returns. One row per movement.</summary>
public class StockIssueAck
{
    public long Id { get; set; }
    public long StockMovementId { get; set; }
    public int QtyReceived { get; set; }
    public int QtyDefective { get; set; }
    public int QtyMissing { get; set; }
    public string? Remarks { get; set; }
    public long AckedByUserId { get; set; }
    public DateTime AckedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Per-serial acknowledgement outcome (issue receipt or peer-transfer receipt).</summary>
public enum SerialAckStatus
{
    Received,
    Defective,
    Missing,
}
