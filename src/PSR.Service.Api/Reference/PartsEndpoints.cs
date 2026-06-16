using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Reference;

public static class PartsEndpoints
{
    private const int MaxPageSize = 200;

    private static readonly string[] PricingRoles =
        { RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.Viewer };

    public static IEndpointRouteBuilder MapPartEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/parts").WithTags("parts").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:long}", GetAsync);
        group.MapGet("/by-code/{code}", GetByCodeAsync);
        group.MapPost("/", CreateAsync).RequireAuthorization("Admin");
        group.MapPut("/{id:long}", UpdateAsync).RequireAuthorization("Admin");
        group.MapPost("/{id:long}/activate", ActivateAsync).RequireAuthorization("Admin");
        group.MapPost("/{id:long}/deactivate", DeactivateAsync).RequireAuthorization("Admin");

        return app;
    }

    private static async Task<Ok<PagedResult<PartDto>>> ListAsync(
        AppDbContext db, ClaimsPrincipal user,
        string? search, bool? activeOnly, int? page, int? pageSize, CancellationToken ct)
    {
        var pageNum = page is null or < 1 ? 1 : page.Value;
        var size = pageSize is null or < 1 or > MaxPageSize ? 50 : pageSize.Value;
        var pricing = CanSeePricing(user);

        var q = db.Parts.AsNoTracking().AsQueryable();
        if (activeOnly == true) q = q.Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(p => p.ItemCode.Contains(s) || p.Name.Contains(s)
                          || (p.Category != null && p.Category.Contains(s)));
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(p => p.ItemCode)
            .Skip((pageNum - 1) * size).Take(size).ToListAsync(ct);

        return TypedResults.Ok(new PagedResult<PartDto>(
            items.Select(p => ToDto(p, pricing)).ToList(), pageNum, size, total));
    }

    private static async Task<Results<Ok<PartDto>, NotFound>> GetAsync(
        long id, AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var p = await db.Parts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return p is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(p, CanSeePricing(user)));
    }

    private static async Task<Results<Ok<PartDto>, NotFound>> GetByCodeAsync(
        string code, AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var p = await db.Parts.AsNoTracking().FirstOrDefaultAsync(x => x.ItemCode == code, ct);
        return p is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(p, CanSeePricing(user)));
    }

    private static async Task<Results<Created<PartDto>, Conflict<string>, ValidationProblem>> CreateAsync(
        [FromBody] CreatePartRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var code = req.ItemCode.Trim();
        if (await db.Parts.AnyAsync(p => p.ItemCode == code, ct))
            return TypedResults.Conflict($"Item code '{code}' already exists.");

        var part = new Part
        {
            ItemCode = code,
            Name = req.Name.Trim(),
            Category = req.Category?.Trim(),
            Unit = req.Unit?.Trim(),
            PurchaseRate = req.PurchaseRate,
            DealerRate = req.DealerRate,
            CustomerRate = req.CustomerRate,
            HsnCode = req.HsnCode?.Trim(),
            GstPercent = req.GstPercent,
            IsSerialTracked = req.IsSerialTracked,
            Remarks = req.Remarks?.Trim(),
        };
        db.Parts.Add(part);

        user.TryGetUserId(out var actor);
        audit.Log(actor, "part.create", "part", null, details: code, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/parts/{part.Id}", ToDto(part, true));
    }

    private static async Task<Results<Ok<PartDto>, NotFound, ValidationProblem>> UpdateAsync(
        long id, [FromBody] UpdatePartRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var p = await db.Parts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return TypedResults.NotFound();

        p.Name = req.Name.Trim();
        p.Category = req.Category?.Trim();
        p.Unit = req.Unit?.Trim();
        p.PurchaseRate = req.PurchaseRate;
        p.DealerRate = req.DealerRate;
        p.CustomerRate = req.CustomerRate;
        p.HsnCode = req.HsnCode?.Trim();
        p.GstPercent = req.GstPercent;
        p.IsSerialTracked = req.IsSerialTracked;
        p.Remarks = req.Remarks?.Trim();

        user.TryGetUserId(out var actor);
        audit.Log(actor, "part.update", "part", id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(ToDto(p, true));
    }

    private static Task<Results<NoContent, NotFound>> ActivateAsync(
        long id, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
        => SetActiveAsync(id, true, user, db, audit, http, ct);

    private static Task<Results<NoContent, NotFound>> DeactivateAsync(
        long id, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
        => SetActiveAsync(id, false, user, db, audit, http, ct);

    private static async Task<Results<NoContent, NotFound>> SetActiveAsync(
        long id, bool active, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var p = await db.Parts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return TypedResults.NotFound();

        p.IsActive = active;
        user.TryGetUserId(out var actor);
        audit.Log(actor, active ? "part.activate" : "part.deactivate", "part", id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static bool CanSeePricing(ClaimsPrincipal user) => PricingRoles.Any(user.IsInRole);

    private static PartDto ToDto(Part p, bool pricing) => new(
        p.Id, p.ItemCode, p.Name, p.Category, p.Unit, p.IsSerialTracked, p.Remarks, p.IsActive,
        pricing ? p.PurchaseRate : null,
        pricing ? p.DealerRate : null,
        pricing ? p.CustomerRate : null,
        pricing ? p.GstPercent : null,
        pricing ? p.HsnCode : null);
}
