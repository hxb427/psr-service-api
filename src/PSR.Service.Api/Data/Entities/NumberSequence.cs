namespace PSR.Service.Api.Data.Entities;

/// <summary>Atomic document-number source. One row per key (e.g. STOCK_REQUEST, STOCK_RETURN).
/// A non-null <see cref="Year"/> makes the sequence year-scoped — it formats as PREFIX-YYYY-NNNN
/// and the counter resets to 1 when the calendar year rolls over (used for PI / Invoice / DC).</summary>
public class NumberSequence
{
    public string Key { get; set; } = string.Empty;   // PK
    public string Prefix { get; set; } = string.Empty;
    public long NextValue { get; set; } = 1;
    public int? Year { get; set; }                      // null = simple PREFIXNNNNN; set = year-scoped PREFIX-YYYY-NNNN
}

public static class SequenceKeys
{
    public const string StockRequest = "STOCK_REQUEST";
    public const string StockReturn = "STOCK_RETURN";
    public const string Service = "SERVICE";

    // Phase 2 field operations (mobile field portal).
    public const string Transfer = "TRANSFER";
    public const string FieldService = "FIELD_SERVICE";
    public const string FieldSale = "FIELD_SALE";

    /// <summary>The engineer call register (CALnnnnn). Its own book rather than a share of
    /// <see cref="FieldService"/>'s: a call is logged whether or not it ends in a service, so hanging
    /// the two off one counter would leave gaps in the service numbering for every demo attended.</summary>
    public const string FieldCall = "FIELD_CALL";

    // Counter/spare sales of warehouse stock (legacy sparesales).
    public const string SpareSale = "SPARE_SALE";
    public const string SpareSaleReturn = "SPARE_SALE_RETURN";

    // Phase 5 — year-scoped document numbers (clean sequential, reset each year).
    public const string ProformaInvoice = "PI";
    public const string Invoice = "INVOICE";
    public const string DeliveryChallan = "DC";

    /// <summary>The challan stock leaves the store on when it is issued to a field technician. Its own
    /// book, not <see cref="DeliveryChallan"/>: that one is the customer's outward challan for a repaired
    /// unit going back, and interleaving the two would put a technician's parts docket in the middle of
    /// the numbering a customer document is audited on.</summary>
    public const string StockIssueDc = "STOCK_ISSUE_DC";
}
