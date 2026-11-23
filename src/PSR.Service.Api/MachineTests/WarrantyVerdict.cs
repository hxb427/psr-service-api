namespace PSR.Service.Api.MachineTests;

/// <summary>IN / OUT / UNKNOWN from a purchase date and a term. One place, because three routes answer
/// the question (a serial lookup, the SN Info search, and a dealer picked by hand) and a unit must read
/// the same in all of them.</summary>
public static class WarrantyVerdict
{
    public const string In = "IN";
    public const string Out = "OUT";
    public const string Unknown = "UNKNOWN";

    public static (string Status, DateTime? Expiry) Compute(DateTime? invoiceDate, int? months)
    {
        if (invoiceDate is not { } inv || months is not { } m || m <= 0) return (Unknown, null);

        // Whole months elapsed, matching the legacy dialog: the anniversary of the invoice is the first
        // day OUT, not the last day IN.
        // The shop's today. On UtcNow the verdict flipped a day early for the five and a half hours
        // after midnight here, which is a warranty claim answered wrongly.
        var today = Common.ShopClock.Today;
        var elapsed = ((today.Year - inv.Year) * 12) + (today.Month - inv.Month);
        if (today.Day < inv.Day) elapsed--;
        return (elapsed < m ? In : Out, inv.AddMonths(m));
    }
}
