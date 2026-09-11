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
}
