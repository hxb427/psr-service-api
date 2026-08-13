namespace PSR.Service.Api.Auth;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "psr-service";
    public string Audience { get; set; } = "psr-service-wpf";
    public string Signing { get; set; } = string.Empty;

    /// <summary>Ceiling on how long an issued token lasts. The nightly cutoff below usually bites
    /// first, so this is the upper bound rather than the number that decides when a session ends.</summary>
    public int ExpiryHours { get; set; } = 24;

    /// <summary>Local hour (0-23) at which every session ends, whatever its remaining lifetime.
    /// No token is ever issued past the next occurrence of this hour, so a session cannot survive
    /// the night and each working day starts with a fresh sign-in. Set at 3am: nobody is at a bench
    /// then, which is the point — the alternative, a fixed 24h from login, comes due at whatever
    /// time the user happened to log in, i.e. in the middle of a shift.</summary>
    public int DailyCutoffLocalHour { get; set; } = 3;

    /// <summary>Offset of the shop's local time from UTC, in hours. 5.5 = IST, which has no DST, so
    /// a plain offset is exact all year. Deliberately not a TimeZoneInfo id: that needs tzdata
    /// present in the container, and a missing time zone database would be a startup crash.</summary>
    public double LocalUtcOffsetHours { get; set; } = 5.5;
}
