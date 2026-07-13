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

    // Phase 5 — year-scoped document numbers (clean sequential, reset each year).
    public const string ProformaInvoice = "PI";
    public const string Invoice = "INVOICE";
    public const string DeliveryChallan = "DC";
}
