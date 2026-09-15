using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Reference;

public static class ServiceChargesEndpoints
{
    public static IEndpointRouteBuilder MapServiceChargeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/service-charges").WithTags("service-charges").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:long}", GetAsync);
        group.MapPost("/", CreateAsync).RequireAuthorization("CatalogueManage");
        group.MapPut("/{id:long}", UpdateAsync).RequireAuthorization("CatalogueManage");
        group.MapPost("/{id:long}/activate", (long id, ClaimsPrincipal u, AppDbContext db, IAuditService a, HttpContext h, CancellationToken ct)
            => SetActiveAsync(id, true, u, db, a, h, ct)).RequireAuthorization("CatalogueManage");
        group.MapPost("/{id:long}/deactivate", (long id, ClaimsPrincipal u, AppDbContext db, IAuditService a, HttpContext h, CancellationToken ct)
            => SetActiveAsync(id, false, u, db, a, h, ct)).RequireAuthorization("CatalogueManage");

        return app;
    }

    /// <summary>The charges the bench reaches for on most jobs, in the order it works through them.
    /// They sit ahead of the alphabetical rest of the list so the technician's add-line dialog opens on
    /// them instead of on whatever happens to start with an A. Matched on a squashed, case-folded name,
    /// so "Sensor Board Replacement" ranks with "sensorboard".</summary>
    internal static readonly string[] LeadingCharges = { "mainboard", "sensorboard", "calibration" };

    private static async Task<Ok<List<ServiceChargeDto>>> ListAsync(AppDbContext db, bool? activeOnly, CancellationToken ct)
    {
        var q = db.ServiceCharges.AsNoTracking().AsQueryable();
        if (activeOnly == true) q = q.Where(x => x.IsActive);
        // Ordered by name in SQL, then re-ranked here: the CASE that would express the priority does not
        // survive translation, and the catalogue is a few dozen rows.
        var items = await q.OrderBy(x => x.Name).ToListAsync(ct);
        return TypedResults.Ok(items.OrderBy(Rank).Select(ToDto).ToList());
    }

    /// <summary>Where a charge sits in the list — its index in <see cref="LeadingCharges"/>, or past the
    /// end of it for everything else. OrderBy is stable, so the rest keeps the name order it arrived in.</summary>
    internal static int Rank(ServiceCharge c)
    {
        var name = Squash(c.Name);
        for (var i = 0; i < LeadingCharges.Length; i++)
            if (name.Contains(LeadingCharges[i], StringComparison.Ordinal)) return i;
        return LeadingCharges.Length;
    }

    private static string Squash(string name) =>
        string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static async Task<Results<Ok<ServiceChargeDto>, NotFound>> GetAsync(long id, AppDbContext db, CancellationToken ct)
    {
        var x = await db.ServiceCharges.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        return x is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(x));
    }

    private static async Task<Results<Created<ServiceChargeDto>, ValidationProblem>> CreateAsync(
        [FromBody] CreateServiceChargeRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var sc = new ServiceCharge
        {
            Name = req.Name.Trim(),
            Charge = req.Charge,
            TaxPercent = req.TaxPercent,
            Remarks = req.Remarks?.Trim(),
        };
        db.ServiceCharges.Add(sc);
        // Saved first so the audit row can carry the id of the charge it created.
        await db.SaveChangesAsync(ct);
        user.TryGetUserId(out var actor);
        audit.Log(actor, "service-charge.create", "service_charge", sc.Id,
            details: $"'{sc.Name}' {sc.Charge:0.##} + {sc.TaxPercent:0.##}% tax", ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/service-charges/{sc.Id}", ToDto(sc));
    }

    private static async Task<Results<Ok<ServiceChargeDto>, NotFound, ValidationProblem>> UpdateAsync(
        long id, [FromBody] UpdateServiceChargeRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var sc = await db.ServiceCharges.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sc is null) return TypedResults.NotFound();
        var name = sc.Name;
        var diff = new AuditDiff();
        diff.Set("name", sc.Name, req.Name, v => sc.Name = v ?? sc.Name);
        diff.Set("charge", sc.Charge, req.Charge, v => sc.Charge = v);
        diff.Set("tax %", sc.TaxPercent, req.TaxPercent, v => sc.TaxPercent = v);
        diff.Set("remarks", sc.Remarks, req.Remarks, v => sc.Remarks = v);

        user.TryGetUserId(out var actor);
        audit.Log(actor, "service-charge.update", "service_charge", id, details: diff.Describe(name), ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(sc));
    }

    private static async Task<Results<NoContent, NotFound>> SetActiveAsync(
        long id, bool active, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var sc = await db.ServiceCharges.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sc is null) return TypedResults.NotFound();
        sc.IsActive = active;
        user.TryGetUserId(out var actor);
        audit.Log(actor, active ? "service-charge.activate" : "service-charge.deactivate", "service_charge", id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static ServiceChargeDto ToDto(ServiceCharge x) => new(x.Id, x.Name, x.Charge, x.TaxPercent, x.Remarks, x.IsActive);
}
