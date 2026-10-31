namespace PSR.Service.Api.Common;

/// <summary>The shop's own calendar, for the dates a person would write on paper.
///
/// Timestamps — when something happened — stay UTC everywhere, and must keep using DateTime.UtcNow.
/// This is for the other kind: a received date, a document date, a sale date. Those are days, not
/// moments, and a person keying one in sends the day off their own wall clock. Defaulting the same
/// column to UtcNow made the two disagree for five and a half hours every evening: anything booked
/// after half past six here was stored, and then read back, as the day before.
///
/// Offset rather than a time zone id, matching JwtOptions.LocalUtcOffsetHours, which this is set
/// from — IST has no DST, so an offset is exact all year, and a tzdata lookup missing from the
/// container would be a startup crash.
///
/// Static because it is one immutable setting for one shop, fixed at startup before the first
/// request and never written again.</summary>
public static class ShopClock
{
    private static double _offsetHours;

    public static void Configure(double offsetHours) => _offsetHours = offsetHours;

    /// <summary>Now, on the shop's wall clock.</summary>
    public static DateTime Now => DateTime.UtcNow.AddHours(_offsetHours);

    /// <summary>Today's date at the shop, at midnight — the same shape a date picker sends.</summary>
    public static DateTime Today => Now.Date;

    /// <summary>The shop day a client meant by the date it sent, whatever shape it sent it in.
    ///
    /// Every business date has to pass through here on the way in, because what arrives is not
    /// reliably the day the person picked. A .NET client whose date carries a zone — and
    /// <c>DateTime.Today</c> does, it is Kind=Local — serialises midnight IST as
    /// "…T00:00:00+05:30". System.Text.Json then converts that to the RECEIVING process's local
    /// time, and this API runs in a UTC container, so it lands as 18:30 on the PREVIOUS day and
    /// used to be stored exactly like that. Every inward entry was booked a day early.
    ///
    /// So the offset is undone rather than trusted: a value that arrived with a zone is taken back
    /// to the shop's wall clock before the time of day is dropped. A value with no zone
    /// (Unspecified) is already a wall-clock day and only needs truncating — which is what a
    /// corrected client sends, so this is a no-op for them and a repair for everyone else.
    ///
    /// Belt and braces on purpose: the client fix alone would leave every copy of the app still in
    /// the field writing bad dates until it updated.</summary>
    public static DateTime BusinessDate(DateTime value) => value.Kind switch
    {
        // An instant. Shift it onto the shop's clock, then keep only the day.
        DateTimeKind.Utc => value.AddHours(_offsetHours).Date,
        // Already converted into this process's zone by the deserialiser; ToUniversalTime undoes
        // exactly that conversion, whatever the server's zone happens to be.
        DateTimeKind.Local => value.ToUniversalTime().AddHours(_offsetHours).Date,
        // A bare wall-clock date, which is what it should have been all along.
        _ => value.Date,
    };

    /// <summary>The nullable form, so a caller can write <c>BusinessDate(req.Date) ?? Today</c>.</summary>
    public static DateTime? BusinessDate(DateTime? value) => value is { } v ? BusinessDate(v) : null;

    /// <summary>The span of UTC instants covered by the shop days <paramref name="from"/> to
    /// <paramref name="to"/>, both inclusive, for filtering a column that holds a TIMESTAMP rather
    /// than a day.
    ///
    /// <see cref="BusinessDate(DateTime)"/> answers "which day did the client mean", which is all a
    /// `date` column needs — a received date is compared against it directly. A created_at is a
    /// different problem: it is an instant stored in UTC, so comparing it against a shop day's
    /// midnight is wrong by the offset in both directions. Asking for the 9th would take in the
    /// evening of the 8th and lose the evening of the 9th — the same off-by-a-day that
    /// <see cref="BusinessDate(DateTime)"/> exists to stop, reappearing one layer down.
    ///
    /// So the shop day's own boundaries are converted instead: the 9th in Kochi begins at 18:30 UTC
    /// on the 8th and ends at 18:30 UTC on the 9th. The upper bound comes back EXCLUSIVE — the start
    /// of the day after the one asked for — because the alternative is an inclusive instant, and
    /// there is no last instant of a day to name: "&lt;= 23:59:59" silently drops whatever was
    /// recorded in the final second.
    ///
    /// Both halves are Unspecified, like everything else read out of and compared against a MySQL
    /// datetime column in this API. The VALUE is the UTC instant, which is what those columns hold.</summary>
    public static (DateTime? FromUtc, DateTime? ToUtcExclusive) UtcWindow(DateTime? from, DateTime? to)
    {
        var f = BusinessDate(from);
        var t = BusinessDate(to);
        return (f is { } fd ? fd.AddHours(-_offsetHours) : null,
                t is { } td ? td.AddDays(1).AddHours(-_offsetHours) : null);
    }
}
