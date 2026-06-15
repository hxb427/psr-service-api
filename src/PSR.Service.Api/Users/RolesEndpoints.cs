using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;

namespace PSR.Service.Api.Users;

public static class RolesEndpoints
{
    public static IEndpointRouteBuilder MapRoleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/roles", ListAsync)
            .WithTags("roles")
            .RequireAuthorization();   // any authenticated user can read the role list (for pickers)

        return app;
    }

    private static async Task<Ok<List<RoleDto>>> ListAsync(AppDbContext db, CancellationToken ct)
    {
        var roles = await db.Roles
            .AsNoTracking()
            .OrderBy(r => r.Id)
            .Select(r => new RoleDto(r.Id, r.Name, r.Description))
            .ToListAsync(ct);

        return TypedResults.Ok(roles);
    }
}
