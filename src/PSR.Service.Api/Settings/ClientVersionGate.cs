using Microsoft.Extensions.Caching.Memory;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Settings;

/// <summary>Server-side enforcement of the minimum client version. The WPF app sends its version
/// in <see cref="HeaderName"/> on every request; anything below the admin-set floor gets
/// 426 Upgrade Required — including login, which is what makes a mandatory update unbypassable.
/// The client-side dialog is UX; this is the gate.</summary>
public static class ClientVersionGate
{
    public const string HeaderName = "X-Client-Version";
    public const string CacheKey = "app:min_client_version";

    /// <summary>Paths an outdated (or headerless) client may still reach: the load balancer /
    /// Docker healthcheck, and the endpoint a blocked client uses to learn what version it needs.</summary>
    private static readonly string[] ExemptPrefixes = ["/health", "/app-version"];

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

    public static IApplicationBuilder UseClientVersionGate(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (ExemptPrefixes.Any(p => path.StartsWithSegments(p)))
            {
                await next();
                return;
            }

            // Same 60s-cache pattern as token-version validation: one DB read a minute, not one
            // per request. The settings PUT evicts the key, so raising the floor applies at once.
            var cache = ctx.RequestServices.GetRequiredService<IMemoryCache>();
            var minRaw = await cache.GetOrCreateAsync(CacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                var settings = ctx.RequestServices.GetRequiredService<AppSettingsService>();
                return settings.GetStringAsync(SettingKeys.MinClientVersion, "0.0.0", ctx.RequestAborted);
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
