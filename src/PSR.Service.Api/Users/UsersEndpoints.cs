using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Users;

public static class UsersEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        // Admin, manager and supervisor all get in; UserHierarchy decides per target what each of
        // them may see and change. Every handler below asks — the policy alone is not the answer.
        var group = app.MapGroup("/users").WithTags("users").RequireAuthorization("UserManage");

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:long}", GetAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id:long}", UpdateAsync);
        group.MapPost("/{id:long}/reset-password", ResetPasswordAsync);
        group.MapPost("/{id:long}/activate", ActivateAsync);
        group.MapPost("/{id:long}/deactivate", DeactivateAsync);
        group.MapPost("/{id:long}/roles", ReplaceRolesAsync);

        return app;
    }

    private static async Task<Ok<List<UserListItemDto>>> ListAsync(
        ClaimsPrincipal principal, AppDbContext db, string? role, CancellationToken ct)
    {
        var query = db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .AsNoTracking()
            .OrderBy(u => u.Username)
            .AsQueryable();

        var users = await query.ToListAsync(ct);

        // Filter by role in memory (small dataset; avoids EF Array.Contains funcletizer issues)
        if (!string.IsNullOrWhiteSpace(role))
            users = users.Where(u => u.UserRoles.Any(ur =>
                string.Equals(ur.Role.Name, role, StringComparison.OrdinalIgnoreCase))).ToList();

        // A supervisor's list simply does not contain managers or other supervisors.
        var actorRank = UserHierarchy.RankOf(principal);
        users = users.Where(u => UserHierarchy.CanView(actorRank, UserHierarchy.RankOf(u))).ToList();

        return TypedResults.Ok(users.Select(u => ToListItem(u, actorRank)).ToList());
    }

    private static async Task<Results<Ok<UserDetailDto>, NotFound>> GetAsync(
        long id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var user = await LoadAsync(db, id, ct);
        if (user is null) return TypedResults.NotFound();

        // 404 rather than 403: an account a supervisor may not see should not be confirmed to exist.
        var actorRank = UserHierarchy.RankOf(principal);
        if (!UserHierarchy.CanView(actorRank, UserHierarchy.RankOf(user))) return TypedResults.NotFound();

        return TypedResults.Ok(ToDetail(user, actorRank));
    }

    private static async Task<Results<Created<UserDetailDto>, Conflict<string>, BadRequest<string>, ValidationProblem>>
        CreateAsync(
            [FromBody] CreateUserRequest req,
            ClaimsPrincipal principal,
            AppDbContext db,
            IAuditService audit,
            HttpContext http,
            CancellationToken ct)
    {
        var roles = await ResolveRolesAsync(db, req.Roles, ct);
        if (roles is null)
            return TypedResults.BadRequest("One or more roles are invalid.");
        if (roles.Count == 0)
            return TypedResults.BadRequest("At least one role is required.");

        // Creating is granting: without this a supervisor could mint an admin account and log into it.
        var actorRank = UserHierarchy.RankOf(principal);
        if (roles.FirstOrDefault(r => !UserHierarchy.CanGrant(actorRank, r.Name)) is { } tooHigh)
            return TypedResults.BadRequest($"You cannot grant the '{tooHigh.Name}' role.");

        var username = req.Username.Trim();
        if (await db.Users.AnyAsync(u => u.Username == username, ct))
            return TypedResults.Conflict($"Username '{username}' already exists.");

        var user = new User
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(req.Password),
            FullName = req.FullName?.Trim(),
            Email = req.Email?.Trim(),
            IsActive = true,
            IsFieldTechnician = req.IsFieldTechnician,
            MustChangePassword = true,
        };
        foreach (var r in roles)
            user.UserRoles.Add(new UserRole { Role = r });

        db.Users.Add(user);

        principal.TryGetUserId(out var actorId);
        audit.Log(actorId, "user.create", "user", null,
            details: $"{username} roles=[{string.Join(',', roles.Select(r => r.Name))}]", ip: http.GetIp());

        await db.SaveChangesAsync(ct);

        var created = await LoadAsync(db, user.Id, ct);
        return TypedResults.Created($"/users/{user.Id}", ToDetail(created!, actorRank));
    }

    private static async Task<Results<Ok<UserDetailDto>, NotFound, ForbidHttpResult, ValidationProblem>> UpdateAsync(
        long id,
        [FromBody] UpdateUserRequest req,
        ClaimsPrincipal principal,
        AppDbContext db,
        IAuditService audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = await LoadAsync(db, id, ct);
        if (user is null) return TypedResults.NotFound();

        var actorRank = UserHierarchy.RankOf(principal);
        if (!UserHierarchy.CanView(actorRank, UserHierarchy.RankOf(user))) return TypedResults.NotFound();
        if (!UserHierarchy.CanManage(actorRank, UserHierarchy.RankOf(user))) return TypedResults.Forbid();

        user.FullName = req.FullName?.Trim();
        user.Email = req.Email?.Trim();
        user.IsFieldTechnician = req.IsFieldTechnician;

        principal.TryGetUserId(out var actorId);
        audit.Log(actorId, "user.update", "user", id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(ToDetail(user, actorRank));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult, ValidationProblem>> ResetPasswordAsync(
        long id,
        [FromBody] ResetPasswordRequest req,
        ClaimsPrincipal principal,
        AppDbContext db,
        IAuditService audit,
        UserTokenVersionCache tvCache,
        HttpContext http,
        CancellationToken ct)
    {
        // Loaded with roles: resetting someone's password is taking their account, so it needs the
        // same rank check as deactivating them.
        var user = await LoadAsync(db, id, ct);
        if (user is null) return TypedResults.NotFound();

        var actorRank = UserHierarchy.RankOf(principal);
        if (!UserHierarchy.CanView(actorRank, UserHierarchy.RankOf(user))) return TypedResults.NotFound();
        if (!UserHierarchy.CanManage(actorRank, UserHierarchy.RankOf(user))) return TypedResults.Forbid();

        user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
        user.MustChangePassword = true;
        user.PasswordChangedAt = null;
        user.TokenVersion++;   // invalidate any active session for that user

        principal.TryGetUserId(out var actorId);
        audit.Log(actorId, "user.reset-password", "user", id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        tvCache.Invalidate(id);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult>> ActivateAsync(
        long id, ClaimsPrincipal principal, AppDbContext db, IAuditService audit,
        HttpContext http, CancellationToken ct)
    {
        var user = await LoadAsync(db, id, ct);
        if (user is null) return TypedResults.NotFound();

        var actorRank = UserHierarchy.RankOf(principal);
        if (!UserHierarchy.CanView(actorRank, UserHierarchy.RankOf(user))) return TypedResults.NotFound();
        if (!UserHierarchy.CanManage(actorRank, UserHierarchy.RankOf(user))) return TypedResults.Forbid();

        user.IsActive = true;
        principal.TryGetUserId(out var actorId);
        audit.Log(actorId, "user.activate", "user", id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> DeactivateAsync(
        long id, ClaimsPrincipal principal, AppDbContext db, IAuditService audit,
        UserTokenVersionCache tvCache, HttpContext http, CancellationToken ct)
    {
        var user = await LoadAsync(db, id, ct);
        if (user is null) return TypedResults.NotFound();

        // The rule this whole hierarchy exists for: a manager must not be able to disable an admin.
        var actorRank = UserHierarchy.RankOf(principal);
        if (!UserHierarchy.CanView(actorRank, UserHierarchy.RankOf(user))) return TypedResults.NotFound();
        if (!UserHierarchy.CanManage(actorRank, UserHierarchy.RankOf(user))) return TypedResults.Forbid();

        principal.TryGetUserId(out var actorId);
        if (actorId == id)
            return TypedResults.BadRequest("You cannot deactivate your own account.");

        if (IsAdmin(user) && !await OtherActiveAdminExistsAsync(db, id, ct))
            return TypedResults.BadRequest("Cannot deactivate the last active admin.");

        user.IsActive = false;
        user.TokenVersion++;   // kick any active session
        audit.Log(actorId, "user.deactivate", "user", id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        tvCache.Invalidate(id);

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<UserDetailDto>, NotFound, ForbidHttpResult, BadRequest<string>>> ReplaceRolesAsync(
        long id,
        [FromBody] AssignRolesRequest req,
        ClaimsPrincipal principal,
        AppDbContext db,
        IAuditService audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = await LoadAsync(db, id, ct);
        if (user is null) return TypedResults.NotFound();

        var actorRank = UserHierarchy.RankOf(principal);
        if (!UserHierarchy.CanView(actorRank, UserHierarchy.RankOf(user))) return TypedResults.NotFound();
        if (!UserHierarchy.CanManage(actorRank, UserHierarchy.RankOf(user))) return TypedResults.Forbid();

        var roles = await ResolveRolesAsync(db, req.Roles, ct);
        if (roles is null)
            return TypedResults.BadRequest("One or more roles are invalid.");
        if (roles.Count == 0)
            return TypedResults.BadRequest("At least one role is required.");

        // Two separate ceilings: outranking the target is not permission to promote them past yourself.
        if (roles.FirstOrDefault(r => !UserHierarchy.CanGrant(actorRank, r.Name)) is { } tooHigh)
            return TypedResults.BadRequest($"You cannot grant the '{tooHigh.Name}' role.");

        var willBeAdmin = roles.Any(r => r.Name == RoleNames.Admin);
        if (IsAdmin(user) && !willBeAdmin && !await OtherActiveAdminExistsAsync(db, id, ct))
            return TypedResults.BadRequest("Cannot remove admin from the last active admin.");

        var desired = roles.Select(r => r.Id).ToHashSet();
        user.UserRoles.RemoveAll(ur => !desired.Contains(ur.RoleId));
        var existing = user.UserRoles.Select(ur => ur.RoleId).ToHashSet();
        foreach (var r in roles.Where(r => !existing.Contains(r.Id)))
            user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = r.Id });

        principal.TryGetUserId(out var actorId);
        audit.Log(actorId, "user.set-roles", "user", id,
            details: string.Join(',', roles.Select(r => r.Name)), ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        var reloaded = await LoadAsync(db, id, ct);
        return TypedResults.Ok(ToDetail(reloaded!, actorRank));
    }

    // ---- helpers ----

    private static Task<User?> LoadAsync(AppDbContext db, long id, CancellationToken ct)
        => db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == id, ct);

    private static bool IsAdmin(User user)
        => user.UserRoles.Any(ur => ur.Role.Name == RoleNames.Admin);

    private static Task<bool> OtherActiveAdminExistsAsync(AppDbContext db, long excludeUserId, CancellationToken ct)
        => db.Users.AnyAsync(u =>
            u.Id != excludeUserId && u.IsActive &&
            u.UserRoles.Any(ur => ur.Role.Name == RoleNames.Admin), ct);

    /// <summary>
    /// Resolves role names to entities. Returns null if any name is unknown.
    /// Loads all roles (9 rows) and filters in memory to dodge the EF Core 9 / .NET 10
    /// Array.Contains funcletizer issue.
    /// </summary>
    private static async Task<List<Role>?> ResolveRolesAsync(AppDbContext db, string[]? requested, CancellationToken ct)
    {
        var names = (requested ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var all = await db.Roles.ToListAsync(ct);
        var matched = all.Where(r => names.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();

        return matched.Count == names.Count ? matched : null;
    }

    private static UserListItemDto ToListItem(User u, int actorRank) => new(
        u.Id, u.Username, u.FullName, u.Email, u.IsActive, u.IsFieldTechnician, u.MustChangePassword, u.LastLoginAt,
        u.UserRoles.Select(ur => ur.Role.Name).OrderBy(n => n).ToArray(),
        UserHierarchy.CanManage(actorRank, UserHierarchy.RankOf(u)));

    private static UserDetailDto ToDetail(User u, int actorRank) => new(
        u.Id, u.Username, u.FullName, u.Email, u.IsActive, u.IsFieldTechnician, u.MustChangePassword, u.LastLoginAt, u.CreatedAt,
        u.UserRoles.Select(ur => ur.Role.Name).OrderBy(n => n).ToArray(),
        UserHierarchy.CanManage(actorRank, UserHierarchy.RankOf(u)));
}
