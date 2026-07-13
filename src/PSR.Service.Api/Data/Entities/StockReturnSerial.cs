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
}
