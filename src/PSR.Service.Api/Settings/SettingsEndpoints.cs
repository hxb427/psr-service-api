using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Settings;

public record AppSettingsDto(bool InvoiceGenerationEnabled);
public record UpdateAppSettingsRequest(bool InvoiceGenerationEnabled);

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
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value ? "true" : "false" });
        else row.Value = value ? "true" : "false";
        await db.SaveChangesAsync(ct);
    }

    public Task<bool> InvoiceGenerationEnabledAsync(CancellationToken ct)
        => GetBoolAsync(SettingKeys.InvoiceGenerationEnabled, true, ct);
}

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings").WithTags("settings").RequireAuthorization();
        group.MapGet("/", GetAsync);
        group.MapPut("/", UpdateAsync).RequireAuthorization("Admin");
        return app;
    }

    private static async Task<Ok<AppSettingsDto>> GetAsync(AppSettingsService settings, CancellationToken ct)
        => TypedResults.Ok(new AppSettingsDto(await settings.InvoiceGenerationEnabledAsync(ct)));

    private static async Task<Ok<AppSettingsDto>> UpdateAsync(
        [FromBody] UpdateAppSettingsRequest req, ClaimsPrincipal user,
        AppSettingsService settings, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        await settings.SetBoolAsync(SettingKeys.InvoiceGenerationEnabled, req.InvoiceGenerationEnabled, ct);
        user.TryGetUserId(out var uid);
        audit.Log(uid, "settings.update", "app_settings", null,
            details: $"{SettingKeys.InvoiceGenerationEnabled}={req.InvoiceGenerationEnabled}", ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new AppSettingsDto(req.InvoiceGenerationEnabled));
    }
}
