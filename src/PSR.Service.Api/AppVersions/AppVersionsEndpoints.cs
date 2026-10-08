using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Settings;

namespace PSR.Service.Api.AppVersions;

/// <summary>Release feed for the mobile clients' in-app updater, plus the admin endpoints that
/// publish into it. Mounted under /app-versions, which <see cref="ClientVersionGate"/> exempts: a
/// client the gate has already turned away still has to be able to find the build that unblocks it.</summary>
public static class AppVersionsEndpoints
{
    public static IEndpointRouteBuilder MapAppVersionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/app-versions").WithTags("app-versions");

        // Anonymous on purpose, like /app-version: the updater runs on the login screen, and a
        // client below the floor cannot authenticate at all.
        group.MapGet("/latest", LatestAsync).AllowAnonymous();

        var admin = group.MapGroup("").RequireAuthorization("Admin");
        admin.MapGet("/", ListAsync);
        admin.MapPost("/", CreateAsync);
        admin.MapPost("/{id:long}/activate", ActivateAsync);
        admin.MapPost("/{id:long}/deactivate", DeactivateAsync);
        admin.MapDelete("/{id:long}", DeleteAsync);

        return app;
    }

    // ----- Public: latest -----

    /// <summary>Newest active build for one client. <paramref name="clientId"/> falls back to the
    /// X-Client-Id header, then to the field portal — the only client that updates this way today.</summary>
    private static async Task<Results<Ok<LatestAppVersionResponse>, NotFound, BadRequest<string>>> LatestAsync(
        AppDbContext db,
        S3PresignService presign,
        HttpContext http,
        CancellationToken ct,
        [FromQuery] string? clientId = null)
    {
        var client = ClientKinds.Normalize(clientId)
                     ?? ClientKinds.Normalize(http.Request.Headers[ClientVersionGate.ClientIdHeaderName].FirstOrDefault())
                     ?? ClientKinds.FieldPortal;

        if (!ClientKinds.All.Contains(client))
            return TypedResults.BadRequest($"Unknown client '{client}'.");

        // Highest active version_code wins; most recently published breaks a tie that shouldn't exist.
        var latest = await db.AppVersions
            .Where(v => v.ClientId == client && v.IsActive)
            .OrderByDescending(v => v.VersionCode)
            .ThenByDescending(v => v.PublishedAt)
            .FirstOrDefaultAsync(ct);

        // 404 = nothing published yet, which the updater reads as "you are up to date".
        if (latest is null) return TypedResults.NotFound();

        var url = await presign.ResolveDownloadUrlAsync(latest.ApkS3Key, ct);
        return TypedResults.Ok(new LatestAppVersionResponse(
            latest.ClientId, latest.Version, latest.VersionCode, url,
            latest.Notes, latest.Mandatory, latest.PublishedAt));
    }

    // ----- Admin -----

    private static async Task<Ok<AppVersionListResponse>> ListAsync(
        AppDbContext db, CancellationToken ct, [FromQuery] string? clientId = null)
    {
        var client = ClientKinds.Normalize(clientId);
        var q = db.AppVersions.AsNoTracking().AsQueryable();
        if (client is not null) q = q.Where(v => v.ClientId == client);

        var rows = await q
            .OrderBy(v => v.ClientId)
            .ThenByDescending(v => v.VersionCode)
            .ToListAsync(ct);
        return TypedResults.Ok(new AppVersionListResponse(rows.Select(ToItem).ToList()));
    }

    private static async Task<Results<Created<AppVersionItem>, BadRequest<string>, Conflict<string>>> CreateAsync(
        [FromBody] CreateAppVersionRequest req,
        AppDbContext db, IAuditService audit, ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        var client = ClientKinds.Normalize(req.ClientId);
        if (client is null || !ClientKinds.All.Contains(client))
            return TypedResults.BadRequest(
                $"Unknown client '{req.ClientId}'. Expected one of: {string.Join(", ", ClientKinds.All)}.");

        if (!ClientVersionGate.TryParse(req.Version, out _))
            return TypedResults.BadRequest($"'{req.Version}' is not a valid version. Use the x.y.z form, e.g. 0.2.0.");

        if (string.IsNullOrWhiteSpace(req.ApkS3Key))
            return TypedResults.BadRequest("An S3 key or download URL is required.");

        // Android refuses an APK whose versionCode doesn't exceed the installed one, so a duplicate
        // would publish an update that fails silently on every device. Reject it here instead.
        if (await db.AppVersions.AnyAsync(v => v.ClientId == client && v.VersionCode == req.VersionCode, ct))
            return TypedResults.Conflict($"Version code {req.VersionCode} is already registered for {client}.");

        var row = new AppVersion
        {
            ClientId = client,
            Version = req.Version.Trim(),
            VersionCode = req.VersionCode,
            ApkS3Key = req.ApkS3Key.Trim(),
            Notes = req.Notes?.Trim(),
            Mandatory = req.Mandatory,
            IsActive = true,
            PublishedAt = DateTime.UtcNow,
        };
        db.AppVersions.Add(row);

        user.TryGetUserId(out var uid);
        audit.Log(uid, "app_version.create", "app_version", null,
            details: $"client={client}, version={row.Version}, code={row.VersionCode}, mandatory={row.Mandatory}",
            ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/app-versions/{row.Id}", ToItem(row));
    }

    private static Task<Results<NoContent, NotFound>> ActivateAsync(
        long id, AppDbContext db, IAuditService audit, ClaimsPrincipal user, HttpContext http, CancellationToken ct)
        => SetActiveAsync(id, true, db, audit, user, http, ct);

    private static Task<Results<NoContent, NotFound>> DeactivateAsync(
        long id, AppDbContext db, IAuditService audit, ClaimsPrincipal user, HttpContext http, CancellationToken ct)
        => SetActiveAsync(id, false, db, audit, user, http, ct);

    /// <summary>Soft-pulls or restores a release. Deactivating the newest row makes the one below it
    /// "latest" again, which is the way back from a bad build that is already on devices.</summary>
    private static async Task<Results<NoContent, NotFound>> SetActiveAsync(
        long id, bool isActive,
        AppDbContext db, IAuditService audit, ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        var row = await db.AppVersions.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (row is null) return TypedResults.NotFound();
        if (row.IsActive == isActive) return TypedResults.NoContent();

        row.IsActive = isActive;
        user.TryGetUserId(out var uid);
        audit.Log(uid, isActive ? "app_version.activate" : "app_version.deactivate",
            "app_version", id, details: $"client={row.ClientId}, code={row.VersionCode}", ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        long id, AppDbContext db, IAuditService audit, ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        var row = await db.AppVersions.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (row is null) return TypedResults.NotFound();

        db.AppVersions.Remove(row);
        user.TryGetUserId(out var uid);
        audit.Log(uid, "app_version.delete", "app_version", id,
            details: $"client={row.ClientId}, code={row.VersionCode}", ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static AppVersionItem ToItem(AppVersion v) => new(
        v.Id, v.ClientId, v.Version, v.VersionCode, v.ApkS3Key, v.Notes, v.Mandatory, v.IsActive, v.PublishedAt);
}
