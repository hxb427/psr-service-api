namespace PSR.Service.Api.Data.Entities;

/// <summary>Where a sale line's rate came from. The party type picks the default column; a manager
/// may still type a one-off price, which is recorded as <see cref="Manual"/> so the register shows
/// that the rate was not the master rate.</summary>
public enum SaleRateType { Customer, Dealer, Manual }

/// <summary>A spare sale runs Pending → Invoiced. Cancelled is only reachable while still Pending —
/// once the invoice exists the goods have left the warehouse and reversing is a stock movement, not
/// a status change.</summary>
public enum SpareSaleStatus { Pending, Invoiced, Cancelled }

/// <summary>A direct (counter) sale of warehouse stock to a dealer or a walk-in customer — items billed
/// as goods rather than fitted as parts on a service job (legacy `sparesales` + generate_sale_pi_page).
///
/// The document chain mirrors the service side: generate a PI, mark payment received, then generate the
/// tax invoice. <b>Stock leaves the warehouse when the INVOICE is generated</b>, not when the sale is
/// saved — so a sale sitting at Pending reserves nothing.</summary>
public class SpareSale : ITimestamps
{
    public long Id { get; set; }
    public string SaleNo { get; set; } = string.Empty;   // unique (SAL00001)
    public DateTime SaleDate { get; set; } = DateTime.UtcNow;

    // The party is EITHER a dealer or a direct customer — never both (same rule as an inward job).
    public string CustomerType { get; set; } = "Direct";   // "Dealer" | "Direct"
    public long? DealerId { get; set; }
    public long? CustomerId { get; set; }

    public SpareSaleStatus Status { get; set; } = SpareSaleStatus.Pending;
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Pending;

    // Stamped back from the generated documents (the gate reads these).
    public string? PiNo { get; set; }
    public DateTime? PiDate { get; set; }
    public string? InvNo { get; set; }
    public DateTime? InvDate { get; set; }

    // Line totals, excluding courier (courier is a per-document charge, entered at generation).
    public decimal TaxableAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public string? Remarks { get; set; }
    public bool IsDeleted { get; set; }

    public long CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<SpareSaleLine> Lines { get; set; } = new();
}

/// <summary>One item on a spare sale. Code/name/HSN/GST are snapshotted at entry so a later edit to the
/// parts master never rewrites history, but <see cref="PartId"/> stays live because the invoice has to
/// decrement that part's warehouse balance.</summary>
public class SpareSaleLine
{
    public long Id { get; set; }
    public long SpareSaleId { get; set; }
    public long PartId { get; set; }

    public string ItemCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? HsnCode { get; set; }
    public string? Unit { get; set; }

    public int Qty { get; set; } = 1;
    public SaleRateType RateType { get; set; } = SaleRateType.Customer;
    /// <summary>Tax-EXCLUSIVE unit rate, matching how the parts master stores rates and how service
    /// lines are priced. Tax is added on top into <see cref="TaxAmount"/>.</summary>
    public decimal UnitRate { get; set; }
    public decimal GstPercent { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }               // taxable + tax

    public SpareSale Sale { get; set; } = null!;
}
