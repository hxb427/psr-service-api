using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Reference;

public static class DealersEndpoints
{
    public static IEndpointRouteBuilder MapDealerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/dealers").WithTags("dealers").RequireAuthorization();

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

    private static async Task<Ok<List<DealerDto>>> ListAsync(AppDbContext db, bool? activeOnly, CancellationToken ct)
    {
        var q = db.Dealers.AsNoTracking().AsQueryable();
        if (activeOnly == true) q = q.Where(x => x.IsActive);
        var items = await q.OrderBy(x => x.Name).ToListAsync(ct);
        return TypedResults.Ok(items.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<DealerDto>, NotFound>> GetAsync(long id, AppDbContext db, CancellationToken ct)
    {
        var x = await db.Dealers.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        return x is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(x));
    }

    private static async Task<Results<Created<DealerDto>, Conflict<string>, ValidationProblem>> CreateAsync(
        [FromBody] CreateDealerRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var name = req.Name.Trim();
        if (await db.Dealers.AnyAsync(d => d.Name == name, ct))
            return TypedResults.Conflict($"Dealer '{name}' already exists.");

        var d = new Dealer { Name = name, WarrantyMonths = req.WarrantyMonths, Remarks = req.Remarks?.Trim() };
        db.Dealers.Add(d);
        user.TryGetUserId(out var actor);
        audit.Log(actor, "dealer.create", "dealer", null, details: name, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/dealers/{d.Id}", ToDto(d));
    }

    private static async Task<Results<Ok<DealerDto>, NotFound, ValidationProblem>> UpdateAsync(
        long id, [FromBody] UpdateDealerRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var d = await db.Dealers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return TypedResults.NotFound();
        d.Name = req.Name.Trim();
        d.WarrantyMonths = req.WarrantyMonths;
        d.Remarks = req.Remarks?.Trim();
        user.TryGetUserId(out var actor);
        audit.Log(actor, "dealer.update", "dealer", id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(d));
    }

    private static async Task<Results<NoContent, NotFound>> SetActiveAsync(
        long id, bool active, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var d = await db.Dealers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return TypedResults.NotFound();
        d.IsActive = active;
        user.TryGetUserId(out var actor);
        audit.Log(actor, active ? "dealer.activate" : "dealer.deactivate", "dealer", id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static DealerDto ToDto(Dealer x) => new(x.Id, x.Name, x.WarrantyMonths, x.Remarks, x.IsActive);
}
