namespace PSR.Service.Api.Data.Entities;

/// <summary>Serial-tracked units on a technician return shipment. Created with the return
/// (serials go IN_TRANSIT_SC); resolved at acknowledgement (RETURNED_TO_SC / DEFECTIVE).</summary>
public class StockReturnSerial
{
    public long Id { get; set; }
    public long StockReturnId { get; set; }
    public long ComponentSerialId { get; set; }
    /// <summary>Technician declared the unit faulty when shipping (COLLECTED/DEFECTIVE stock).</summary>
    public bool Defective { get; set; }

    /// <summary>What the service centre found when it opened the box, per unit; null until that unit
    /// is booked in. The shipment is the envelope, not the thing being received - units in one box
    /// can arrive in different states, and one of them missing must not hold the other nine out of
    /// stock. The shipment closes when the last unit here is resolved.</summary>
    public SerialAckStatus? AckStatus { get; set; }
    public long? AckedByUserId { get; set; }
    public DateTime? AckedAt { get; set; }
}
