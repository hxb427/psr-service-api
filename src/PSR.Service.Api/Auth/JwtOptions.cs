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
    /// time the user happened to log in, i.e. in the middle of a shift.
    ///
    /// Applies to the desktop client only. See <see cref="FieldPortalDailyCutoff"/>.</summary>
    public int DailyCutoffLocalHour { get; set; } = 3;

    /// <summary>How long a token issued to the Android field portal lasts. A week, against the
    /// desktop's day, because the two clients are not the same kind of machine: a bench PC is shared
    /// and stays on the premises, a phone is one technician's and goes home in their pocket. The
    /// nightly cutoff was protecting the first case and only ever charged the second — a technician
    /// standing in a customer's plant on one bar of signal, retyping a password to file the job they
    /// just finished.
    ///
    /// Per-client rather than one global number for the same reason the version floor is
    /// (<see cref="Settings.ClientVersionGate"/>): one value cannot be right for both, and raising it
    /// for the phone must not quietly leave desktop sessions alive overnight.</summary>
    public int FieldPortalExpiryHours { get; set; } = 168;

    /// <summary>Whether <see cref="DailyCutoffLocalHour"/> also caps field portal tokens. Off, since
    /// a week-long token that still dies at 3am is a day-long token — the cutoff would clamp every
    /// one of them and <see cref="FieldPortalExpiryHours"/> would never be reached. Kept as a switch
    /// so the old behaviour is one environment variable away if a week signed in on a phone turns out
    /// to be more than the shop wants to carry.</summary>
    public bool FieldPortalDailyCutoff { get; set; } = false;

    /// <summary>Offset of the shop's local time from UTC, in hours. 5.5 = IST, which has no DST, so
    /// a plain offset is exact all year. Deliberately not a TimeZoneInfo id: that needs tzdata
    /// present in the container, and a missing time zone database would be a startup crash.</summary>
    public double LocalUtcOffsetHours { get; set; } = 5.5;
}
