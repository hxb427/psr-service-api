using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Settings;

/// <summary><paramref name="InvoiceGenerationEnabled"/> governs SERVICE invoices and keeps its original
/// name so an older client reading this payload still gates the right thing.</summary>
public record AppSettingsDto(
    bool InvoiceGenerationEnabled, bool SaleInvoiceGenerationEnabled,
    string MinClientVersion, int DefaultWarrantyMonths);

/// <summary>Null fields are left untouched, which is what lets an older client that does not know about
/// the sale switch save the settings it does know without silently turning the other one off.</summary>
public record UpdateAppSettingsRequest(
    bool InvoiceGenerationEnabled, bool? SaleInvoiceGenerationEnabled,
    string? MinClientVersion, int? DefaultWarrantyMonths);

/// <summary>What a client — possibly one too old to log in — may learn anonymously: the version
/// floor. Served on /app-version, which the version gate exempts.</summary>
public record AppVersionDto(string MinClientVersion);

/// <summary>Reads/writes admin feature toggles. Anyone authenticated can read (so clients can grey out
/// disabled actions); only admins can change them.</summary>
public class AppSettingsService(AppDbContext db)
{
    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct)
    {
        var v = await db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return v is null ? fallback : v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetBoolAsync(string key, bool value, CancellationToken ct)
        => await SetStringAsync(key, value ? "true" : "false", ct);

    public async Task<string> GetStringAsync(string key, string fallback, CancellationToken ct)
    {
        var v = await db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return v ?? fallback;
    }

    public async Task SetStringAsync(string key, string value, CancellationToken ct)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync(ct);
    }

    public Task<bool> InvoiceGenerationEnabledAsync(CancellationToken ct)
        => GetBoolAsync(SettingKeys.InvoiceGenerationEnabled, true, ct);

    public Task<bool> SaleInvoiceGenerationEnabledAsync(CancellationToken ct)
        => GetBoolAsync(SettingKeys.SaleInvoiceGenerationEnabled, true, ct);

    public Task<string> MinClientVersionAsync(CancellationToken ct)
        => GetStringAsync(SettingKeys.MinClientVersion, "0.0.0", ct);

    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct)
    {
        var v = await db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(v, out var i) ? i : fallback;
    }

    public Task<int> DefaultWarrantyMonthsAsync(CancellationToken ct)
        => GetIntAsync(SettingKeys.DefaultWarrantyMonths, 0, ct);
}

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings").WithTags("settings").RequireAuthorization();
        group.MapGet("/", GetAsync);
        group.MapPut("/", UpdateAsync).RequireAuthorization("SettingsManage");

        // Anonymous on purpose: a client below the floor can't authenticate (login is gated too),
        // yet still needs to learn which version it must update to.
        app.MapGet("/app-version", AppVersionAsync).WithTags("settings");
        return app;
    }

    private static async Task<Ok<AppVersionDto>> AppVersionAsync(AppSettingsService settings, CancellationToken ct)
        => TypedResults.Ok(new AppVersionDto(await settings.MinClientVersionAsync(ct)));

    private static async Task<Ok<AppSettingsDto>> GetAsync(AppSettingsService settings, CancellationToken ct)
        => TypedResults.Ok(new AppSettingsDto(
            await settings.InvoiceGenerationEnabledAsync(ct),
            await settings.SaleInvoiceGenerationEnabledAsync(ct),
            await settings.MinClientVersionAsync(ct),
            await settings.DefaultWarrantyMonthsAsync(ct)));

    private static async Task<Results<Ok<AppSettingsDto>, BadRequest<string>>> UpdateAsync(
        [FromBody] UpdateAppSettingsRequest req, ClaimsPrincipal user,
        AppSettingsService settings, AppDbContext db, IAuditService audit, IMemoryCache cache,
        HttpContext http, CancellationToken ct)
    {
        // Null = older client that doesn't know the field; leave the floor untouched.
        // Empty = clear the floor. Anything else must parse, or a typo like "1..2" would
        // lock every client out of the API at once.
        string? minToStore = null;
        if (req.MinClientVersion is not null)
        {
            var trimmed = req.MinClientVersion.Trim();
            if (trimmed.Length == 0) minToStore = "0.0.0";
            else if (ClientVersionGate.TryParse(trimmed, out var parsed)) minToStore = parsed.ToString(3);
            else return TypedResults.BadRequest($"'{req.MinClientVersion}' is not a valid version. Use the x.y.z form, e.g. 1.2.0.");
        }

        // Null = older client that doesn't send the field. Negative is meaningless, and an absurd
        // figure would silently mark decade-old machines in warranty, so cap it at 50 years.
        if (req.DefaultWarrantyMonths is { } dwm && (dwm < 0 || dwm > 600))
            return TypedResults.BadRequest("Default warranty months must be between 0 and 600 (0 = no fallback).");

        // Managers may work the invoice switches, not the two settings that reach past billing: the
        // version floor can lock every client out of the API, and the warranty default silently
        // changes what the whole estate calls in-warranty. Compared against what is stored rather
        // than rejected on presence — the console posts every field on each save, so a manager
        // re-sending today's values must not be treated as an attempt to change them.
        var isAdmin = user.IsInRole(RoleNames.Admin);
        if (!isAdmin)
        {
            if (minToStore is not null && minToStore != await settings.MinClientVersionAsync(ct))
                return TypedResults.BadRequest("Only an admin can change the minimum allowed app version.");
            if (req.DefaultWarrantyMonths is { } wanted && wanted != await settings.DefaultWarrantyMonthsAsync(ct))
                return TypedResults.BadRequest("Only an admin can change the default warranty length.");
        }

        await settings.SetBoolAsync(SettingKeys.InvoiceGenerationEnabled, req.InvoiceGenerationEnabled, ct);
        if (req.SaleInvoiceGenerationEnabled is { } saleFlag)
            await settings.SetBoolAsync(SettingKeys.SaleInvoiceGenerationEnabled, saleFlag, ct);
        if (req.DefaultWarrantyMonths is { } months && isAdmin)
            await settings.SetStringAsync(SettingKeys.DefaultWarrantyMonths, months.ToString(), ct);
        if (minToStore is not null && isAdmin)
        {
            await settings.SetStringAsync(SettingKeys.MinClientVersion, minToStore, ct);
            // The gate caches the floor for 60s; evicting makes a raise bite immediately.
            cache.Remove(ClientVersionGate.CacheKey);
        }

        user.TryGetUserId(out var uid);
        audit.Log(uid, "settings.update", "app_settings", null,
            details: $"{SettingKeys.InvoiceGenerationEnabled}={req.InvoiceGenerationEnabled}"
                   + (req.SaleInvoiceGenerationEnabled is { } sf ? $", {SettingKeys.SaleInvoiceGenerationEnabled}={sf}" : "")
                   + (minToStore is not null ? $", {SettingKeys.MinClientVersion}={minToStore}" : "")
                   + (req.DefaultWarrantyMonths is { } d ? $", {SettingKeys.DefaultWarrantyMonths}={d}" : ""),
            ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(new AppSettingsDto(
            req.InvoiceGenerationEnabled,
            await settings.SaleInvoiceGenerationEnabledAsync(ct),
            await settings.MinClientVersionAsync(ct),
            await settings.DefaultWarrantyMonthsAsync(ct)));
    }
}
