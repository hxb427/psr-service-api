namespace PSR.Service.Api.Data.Entities;

/// <summary>Atomic document-number source. One row per key (e.g. STOCK_REQUEST, STOCK_RETURN).</summary>
public class NumberSequence
{
    public string Key { get; set; } = string.Empty;   // PK
    public string Prefix { get; set; } = string.Empty;
    public long NextValue { get; set; } = 1;
}

public static class SequenceKeys
{
    public const string StockRequest = "STOCK_REQUEST";
    public const string StockReturn = "STOCK_RETURN";
}
