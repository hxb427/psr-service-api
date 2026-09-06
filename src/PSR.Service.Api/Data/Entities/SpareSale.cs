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
/// tax invoice. <b>None of it touches stock.</b> The goods leave on one explicit action — Mark as sold,
/// which stamps <see cref="SoldAt"/> and books the warehouse movement. Paperwork and stock were welded
/// together before: the invoice both billed the customer and emptied the shelf, so a sale invoiced for
/// goods handed over last week moved the stock a week late, and a sale settled in cash with no invoice
/// never moved it at all. Splitting them lets the two happen in either order, or only one of them.
///
/// A sale that has not been marked sold reserves nothing, but it does count as a claim on the stock
/// (see SpareSaleService.CommittedQuery) so the same units are not sold twice over.</summary>
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

    /// <summary>When the goods actually left the warehouse. Null until someone marks the sale sold;
    /// set is the ONLY state in which this sale has moved stock, and it is what the edit, cancel and
    /// return rules read. Nothing else — payment, PI, invoice — writes it.</summary>
    public DateTime? SoldAt { get; set; }
    public long? SoldByUserId { get; set; }

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

/// <summary>Goods coming back from an invoiced sale.
///
/// A return is its own record rather than a status on the sale, because it is a separate event with its
/// own date, reason and quantities — a customer may bring back two of the five they bought, and may do
/// it twice. The sale itself is never rewritten: it still says what was sold and invoiced, and the
/// returns hanging off it say what came back. That is what makes the pair auditable, and it is why
/// cancelling an invoiced sale stays forbidden.
///
/// Stock goes back to the warehouse when the return is recorded, through the ledger, so the movement
/// carries a SALE_RETURN reference instead of an anonymous adjustment.</summary>
public class SpareSaleReturn : ITimestamps
{
    public long Id { get; set; }
    public long SpareSaleId { get; set; }
    public string ReturnNo { get; set; } = string.Empty;   // unique (SRT00001)
    public DateTime ReturnDate { get; set; } = DateTime.UtcNow;

    /// <summary>Why it came back. Required — a return that moves stock without a reason is the thing
    /// a stock audit cannot explain later.</summary>
    public string Reason { get; set; } = string.Empty;

    public long CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<SpareSaleReturnLine> Lines { get; set; } = new();
}

/// <summary>One part coming back on a return. Code is snapshotted like the sale line's, so the record
/// still reads correctly after a parts-master rename.</summary>
public class SpareSaleReturnLine
{
    public long Id { get; set; }
    public long SpareSaleReturnId { get; set; }
    public long PartId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public int Qty { get; set; }

    public SpareSaleReturn Return { get; set; } = null!;
}
