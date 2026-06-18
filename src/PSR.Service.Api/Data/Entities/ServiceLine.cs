namespace PSR.Service.Api.Data.Entities;

/// <summary>One line on a service job (replaces the encoded COMPONENTS string).</summary>
public class ServiceLine
{
    public long Id { get; set; }
    public long ServiceId { get; set; }
    public ServiceLineType LineType { get; set; }
    public long? PartId { get; set; }            // Component / Replacement
    public long? ServiceChargeId { get; set; }   // ServiceCharge
    public string? Description { get; set; }
    public int Qty { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string? ReplacementSerialNo { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ServiceJob Service { get; set; } = null!;
}
