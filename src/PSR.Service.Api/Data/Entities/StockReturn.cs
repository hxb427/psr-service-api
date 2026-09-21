namespace PSR.Service.Api.Data.Entities;

public class StockReturn : ITimestamps
{
    public long Id { get; set; }
    public string ReturnNo { get; set; } = string.Empty;   // unique
    public long TechnicianId { get; set; }
    public long PartId { get; set; }
    public int Qty { get; set; }
    /// <summary>Defaults to GoodStock so rows written before this column existed keep the old meaning.</summary>
    public StockReturnKind Kind { get; set; } = StockReturnKind.GoodStock;

    /// <summary>True when the technician's balance was debited as the shipment left them, which is
    /// what new good-stock returns do. False for shipments raised before dispatch and receipt were
    /// split out — those are still carrying their quantity, so acknowledging one has to debit it.</summary>
    public bool TechnicianDebitedOnShip { get; set; }
    public StockReturnStatus Status { get; set; } = StockReturnStatus.Pending;
    public long? AcknowledgedByUserId { get; set; }
    public DateTime? AcknowledgedDate { get; set; }
    public string? Remarks { get; set; }

    // Field-technician shipment details (legacy technician_return_dispatches).
    public string? Courier { get; set; }
    public string? TrackingNo { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
