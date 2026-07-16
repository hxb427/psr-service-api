using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Users;

namespace PSR.Service.Api.Audit;

public static class AuditEndpoints
{
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/audit", ListAsync)
            .WithTags("audit")
            .RequireAuthorization("Admin");

        return app;
    }

    private static async Task<Ok<PagedResult<AuditLogItemDto>>> ListAsync(
        AppDbContext db,
        long? userId,
        string? action,
        DateTime? from,
        DateTime? to,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        var pageNum = page is null or < 1 ? 1 : page.Value;
        var size = pageSize is null or < 1 or > MaxPageSize ? 50 : pageSize.Value;

        var query = db.AuditLog.AsNoTracking().AsQueryable();

        if (userId is not null) query = query.Where(a => a.UserId == userId);
        if (!string.IsNullOrWhiteSpace(action))
        {
            var term = action.Trim();
            query = query.Where(a => a.Action.Contains(term));
        }
        if (from is not null) query = query.Where(a => a.CreatedAt >= from);
        if (to is not null) query = query.Where(a => a.CreatedAt <= to);

        var total = await query.CountAsync(ct);

        // Left-join username for display.
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((pageNum - 1) * size)
            .Take(size)
            .Select(a => new AuditLogItemDto(
                a.Id,
                a.UserId,
                db.Users.Where(u => u.Id == a.UserId).Select(u => u.Username).FirstOrDefault(),
                a.Action,
                a.Entity,
                a.EntityId,
                a.Details,
                a.IpAddress,
                a.CreatedAt))
            .ToListAsync(ct);

        return TypedResults.Ok(new PagedResult<AuditLogItemDto>(items, pageNum, size, total));
    }
}
