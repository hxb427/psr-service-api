namespace PSR.Service.Api.Data.Entities;

/// <summary>On-site field service performed by a field technician (legacy service_records).
/// Separate from the shop-job workflow (services): no state machine — a completed fact.
/// Creating one consumes the technician's stock and drives serial transitions.</summary>
public class FieldService
{
    public long Id { get; set; }
    public string ServiceNo { get; set; } = string.Empty;   // unique (FSVnnnnn)
    public long TechnicianId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Place { get; set; }
    public string? MachineSerial { get; set; }              // serviced unit's serial (free text)
    public string? Complaint { get; set; }
    public string? WorkDone { get; set; }
    public string? Remarks { get; set; }
    public long CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<FieldServiceLine> Lines { get; set; } = new();
}

public enum FieldLineKind
{
    Used,       // part fitted/consumed from technician stock
    Collected,  // faulty unit collected from the customer (no stock consumption)
}

public class FieldServiceLine
{
    public long Id { get; set; }
    public long FieldServiceId { get; set; }
    public FieldLineKind Kind { get; set; } = FieldLineKind.Used;
    public long PartId { get; set; }
    public int Qty { get; set; } = 1;
    public decimal UnitPrice { get; set; }                  // server-set from CustomerRate; hidden from techs
    public decimal Amount { get; set; }
    /// <summary>Used: serial fitted (serial-tracked parts, one unit per line).
    /// Collected: serial of the faulty unit taken from the customer.</summary>
    public string? SerialNo { get; set; }
    /// <summary>Collected lines: technician declared the collected unit faulty.</summary>
    public bool Defective { get; set; }

    public FieldService FieldService { get; set; } = null!;
}

/// <summary>Direct field sale by a field technician (legacy sales_transactions).</summary>
public class FieldSale
{
    public long Id { get; set; }
    public string SaleNo { get; set; } = string.Empty;      // unique (FSLnnnnn)
    public long TechnicianId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Place { get; set; }
    public string? Remarks { get; set; }
    public long CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<FieldSaleLine> Lines { get; set; } = new();
}

public class FieldSaleLine
{
    public long Id { get; set; }
    public long FieldSaleId { get; set; }
    public long PartId { get; set; }
    public int Qty { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string? SerialNo { get; set; }                   // serial sold (tracked parts, one unit per line)

    public FieldSale FieldSale { get; set; } = null!;
}
