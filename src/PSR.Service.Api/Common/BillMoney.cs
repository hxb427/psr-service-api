namespace PSR.Service.Api.Common;

/// <summary>The rounding rule for a figure somebody pays.
///
/// Line amounts and tax keep their paise — they are what the GST is computed on and they have to
/// reproduce each other. Only the grand total is rounded, and it is rounded UP to the next whole rupee:
/// the counter does not hand back paise and never asks for them either, so a bill of 7,330.47 is
/// collected as 7,331.
///
/// The difference is not hidden. A document prints it as a Round off line, derived from the gap between
/// its own total and what its parts add up to, so the figures on the page still tie.</summary>
public static class BillMoney
{
    /// <summary>The payable figure: up to the next whole rupee. Already-whole amounts are untouched, and a
    /// zero total stays zero rather than becoming a rupee.</summary>
    public static decimal RoundUp(decimal amount) => decimal.Ceiling(amount);
}
