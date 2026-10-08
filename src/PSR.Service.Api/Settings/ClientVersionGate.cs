using Microsoft.Extensions.Caching.Memory;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Settings;

/// <summary>The clients that talk to this API, as sent in
/// <see cref="ClientVersionGate.ClientIdHeaderName"/>. Each carries its own version floor: the WPF
/// desktop app and the Android field portal are numbered independently and always will be, so a
/// single global floor can only ever be right for one of them. Raising the desktop floor to 1.2.0
/// must not reach across and brick a field portal sitting at 0.1.0.</summary>
public static class ClientKinds
{
    public const string Wpf = "wpf";
    public const string FieldPortal = "field-portal";

    public static readonly string[] All = [Wpf, FieldPortal];

    /// <summary>Lower-cases and trims an incoming id. Null/blank stays null, which
    /// <see cref="ClientVersionGate.SettingKeyFor"/> reads as "the legacy desktop client".</summary>
    public static string? Normalize(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant();
}

/// <summary>Server-side enforcement of the minimum client version. Every client sends its version in
/// <see cref="HeaderName"/> and its identity in <see cref="ClientIdHeaderName"/>; anything below the
/// floor admin-set for *that client* gets 426 Upgrade Required — including login, which is what makes
/// a mandatory update unbypassable. The client-side dialog is UX; this is the gate.</summary>
public static class ClientVersionGate
{
    public const string HeaderName = "X-Client-Version";

    /// <summary>Which app is calling. Absent on desktop builds that shipped before per-client floors
    /// existed, which is exactly why a missing id reads as <see cref="ClientKinds.Wpf"/>.</summary>
    public const string ClientIdHeaderName = "X-Client-Id";

    public const string CacheKeyPrefix = "app:min_client_version:";

    /// <summary>Paths an outdated (or headerless) client may still reach: the load balancer / Docker
    /// healthcheck, the endpoint a blocked client uses to learn what version it needs, and the
    /// app-versions feed it downloads the update from. That last one is the difference between a
    /// mandatory update and a brick — a blocked client has to be able to fetch its own way out.</summary>
    private static readonly string[] ExemptPrefixes = ["/health", "/app-version", "/app-versions"];

    /// <summary>Lenient SemVer-ish parse: optional leading v, prerelease/build suffixes ignored
    /// ("1.2.0-beta+abc" reads as 1.2.0). Releases are plain x.y.z, so numeric compare is enough —
    /// and it means a dev build's "0.0.0-dev" doesn't sort below a floor of exactly 0.0.0.</summary>
    public static bool TryParse(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+']);
        if (cut > 0) s = s[..cut];
        if (s.IndexOf('.') < 0) s += ".0";           // Version.TryParse rejects a bare "2"

        if (!Version.TryParse(s, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>Which settings row holds the floor for a given client id.
    ///
    /// Missing, blank or unrecognised ids resolve to the original <see cref="SettingKeys.MinClientVersion"/>
    /// key. Two reasons: desktop builds already in the field don't send the header at all and must keep
    /// behaving exactly as they did, and an id nobody recognises should land on the strictest floor we
    /// have rather than slip through — an unknown id is not a way around the gate.</summary>
    public static string SettingKeyFor(string? clientId) => ClientKinds.Normalize(clientId) switch
    {
        ClientKinds.FieldPortal => SettingKeys.MinFieldPortalVersion,
        _ => SettingKeys.MinClientVersion,
    };

    public static string CacheKeyFor(string settingKey) => CacheKeyPrefix + settingKey;

    /// <summary>Drops every cached floor. The settings PUT calls this so raising a floor bites at once
    /// instead of up to 60s later; cheap enough that evicting all of them beats tracking which changed.</summary>
    public static void EvictCachedFloors(IMemoryCache cache)
    {
        cache.Remove(CacheKeyFor(SettingKeys.MinClientVersion));
        cache.Remove(CacheKeyFor(SettingKeys.MinFieldPortalVersion));
    }

    public static IApplicationBuilder UseClientVersionGate(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (ExemptPrefixes.Any(p => path.StartsWithSegments(p)))
            {
                await next();
                return;
            }

            var clientId = ctx.Request.Headers[ClientIdHeaderName].FirstOrDefault();
            var settingKey = SettingKeyFor(clientId);

            // Same 60s-cache pattern as token-version validation: one DB read a minute per client,
            // not one per request. The settings PUT evicts the keys, so raising a floor applies at once.
            var cache = ctx.RequestServices.GetRequiredService<IMemoryCache>();
            var minRaw = await cache.GetOrCreateAsync(CacheKeyFor(settingKey), entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                var settings = ctx.RequestServices.GetRequiredService<AppSettingsService>();
                return settings.GetStringAsync(settingKey, "0.0.0", ctx.RequestAborted);
            });

            if (!TryParse(minRaw, out var minimum) || minimum == new Version(0, 0, 0))
            {
                await next();
                return;
            }

            // No/garbled header counts as 0.0.0 — an outdated build that predates the header must
            // not slip past the floor. (Server-to-server tools like curl must send the header once
            // a floor is set; that is also the recovery path if an admin sets a floor too high.)
            TryParse(ctx.Request.Headers[HeaderName].FirstOrDefault(), out var client);

            if (client < minimum)
            {
                ctx.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = $"This app version is no longer supported. Update to {minRaw} or newer to continue.",
                    minClientVersion = minRaw,
                });
                return;
            }

            await next();
        });
}
