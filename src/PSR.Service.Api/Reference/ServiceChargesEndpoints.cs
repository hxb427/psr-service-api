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
        group.MapPost("/", CreateAsync).RequireAuthorization("Admin");
        group.MapPut("/{id:long}", UpdateAsync).RequireAuthorization("Admin");
        group.MapPost("/{id:long}/activate", (long id, ClaimsPrincipal u, AppDbContext db, IAuditService a, HttpContext h, CancellationToken ct)
            => SetActiveAsync(id, true, u, db, a, h, ct)).RequireAuthorization("Admin");
        group.MapPost("/{id:long}/deactivate", (long id, ClaimsPrincipal u, AppDbContext db, IAuditService a, HttpContext h, CancellationToken ct)
            => SetActiveAsync(id, false, u, db, a, h, ct)).RequireAuthorization("Admin");

        return app;
    }

    private static async Task<Ok<List<ServiceChargeDto>>> ListAsync(AppDbContext db, bool? activeOnly, CancellationToken ct)
    {
        var q = db.ServiceCharges.AsNoTracking().AsQueryable();
        if (activeOnly == true) q = q.Where(x => x.IsActive);
        var items = await q.OrderBy(x => x.Name).ToListAsync(ct);
        return TypedResults.Ok(items.Select(ToDto).ToList());
    }

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
        user.TryGetUserId(out var actor);
        audit.Log(actor, "service-charge.create", "service_charge", null, details: sc.Name, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/service-charges/{sc.Id}", ToDto(sc));
    }

    private static async Task<Results<Ok<ServiceChargeDto>, NotFound, ValidationProblem>> UpdateAsync(
        long id, [FromBody] UpdateServiceChargeRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var sc = await db.ServiceCharges.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sc is null) return TypedResults.NotFound();
        sc.Name = req.Name.Trim();
        sc.Charge = req.Charge;
        sc.TaxPercent = req.TaxPercent;
        sc.Remarks = req.Remarks?.Trim();
        user.TryGetUserId(out var actor);
        audit.Log(actor, "service-charge.update", "service_charge", id, ip: http.GetIp());
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
