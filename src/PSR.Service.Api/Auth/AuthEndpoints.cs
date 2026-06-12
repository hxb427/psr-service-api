using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapGet("/me", MeAsync).RequireAuthorization();

        return app;
    }

    private static async Task<Results<Ok<LoginResponse>, UnauthorizedHttpResult, ValidationProblem>>
        LoginAsync(
            [FromBody] LoginRequest req,
            AppDbContext db,
            JwtTokenService jwt,
            UserTokenVersionCache tvCache,
            HttpContext http,
            CancellationToken ct)
    {
        var user = await db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Username == req.Username, ct);

        if (user is null || !user.IsActive || !PasswordHasher.Verify(req.Password, user.PasswordHash))
        {
            await LogAuditAsync(db, null, "auth.login.failed", details: req.Username, http.GetIp(), ct);
            await db.SaveChangesAsync(ct);
            return TypedResults.Unauthorized();
        }

        user.TokenVersion++;
        user.LastLoginAt = DateTime.UtcNow;

        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToArray();
        var (token, expires) = jwt.Issue(user, roles);

        await LogAuditAsync(db, user.Id, "auth.login.ok", ip: http.GetIp(), ct: ct);
        await db.SaveChangesAsync(ct);
        tvCache.Invalidate(user.Id);

        return TypedResults.Ok(new LoginResponse(
            token, expires, user.Id, user.Username, user.FullName, roles, user.MustChangePassword));
    }

    private static async Task<Results<Ok<ChangePasswordResponse>, UnauthorizedHttpResult, BadRequest<string>>>
        ChangePasswordAsync(
            [FromBody] ChangePasswordRequest req,
            ClaimsPrincipal principal,
            AppDbContext db,
            JwtTokenService jwt,
            UserTokenVersionCache tvCache,
            HttpContext http,
            CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
            return TypedResults.Unauthorized();

        var user = await db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null || !user.IsActive)
            return TypedResults.Unauthorized();

        if (!PasswordHasher.Verify(req.CurrentPassword, user.PasswordHash))
        {
            await LogAuditAsync(db, user.Id, "auth.password.change.failed", ip: http.GetIp(), ct: ct);
            await db.SaveChangesAsync(ct);
            return TypedResults.BadRequest("Current password is incorrect.");
        }

        if (req.NewPassword == req.CurrentPassword)
            return TypedResults.BadRequest("New password must differ from current password.");

        user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
        user.MustChangePassword = false;
        user.PasswordChangedAt = DateTime.UtcNow;
        user.TokenVersion++;

        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToArray();
        var (token, expires) = jwt.Issue(user, roles);

        await LogAuditAsync(db, user.Id, "auth.password.change.ok", ip: http.GetIp(), ct: ct);
        await db.SaveChangesAsync(ct);
        tvCache.Invalidate(user.Id);

        return TypedResults.Ok(new ChangePasswordResponse(token, expires));
    }

    private static async Task<Results<NoContent, UnauthorizedHttpResult>> LogoutAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        UserTokenVersionCache tvCache,
        HttpContext http,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
            return TypedResults.Unauthorized();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return TypedResults.Unauthorized();

        user.TokenVersion++;
        await LogAuditAsync(db, user.Id, "auth.logout", ip: http.GetIp(), ct: ct);
        await db.SaveChangesAsync(ct);
        tvCache.Invalidate(user.Id);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<object>, UnauthorizedHttpResult>> MeAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var userId))
            return TypedResults.Unauthorized();

        var user = await db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null) return TypedResults.Unauthorized();

        return TypedResults.Ok<object>(new
        {
            user.Id,
            user.Username,
            user.FullName,
            user.Email,
            user.MustChangePassword,
            Roles = user.UserRoles.Select(ur => ur.Role.Name).ToArray()
        });
    }

    private static async Task LogAuditAsync(
        AppDbContext db, long? userId, string action,
        string? details = null, string? ip = null, CancellationToken ct = default)
    {
        db.AuditLog.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            Details = details,
            IpAddress = ip,
        });
        await Task.CompletedTask;
    }
}

internal static class ClaimsPrincipalExtensions
{
    public static bool TryGetUserId(this ClaimsPrincipal principal, out long userId)
    {
        userId = 0;
        var sub = principal.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub");
        return long.TryParse(sub, out userId);
    }
}

internal static class HttpContextExtensions
{
    public static string? GetIp(this HttpContext ctx)
        => ctx.Connection.RemoteIpAddress?.ToString();
}
