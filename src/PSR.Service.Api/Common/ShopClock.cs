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
}
