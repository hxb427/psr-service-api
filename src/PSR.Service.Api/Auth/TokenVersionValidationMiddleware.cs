using System.Security.Claims;
using PSR.Service.Api.Data;

namespace PSR.Service.Api.Auth;

public class TokenVersionValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TokenVersionValidationMiddleware> _logger;

    public TokenVersionValidationMiddleware(
        RequestDelegate next,
        ILogger<TokenVersionValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx, AppDbContext db, UserTokenVersionCache cache)
    {
        if (ctx.User.Identity?.IsAuthenticated != true)
        {
            await _next(ctx);
            return;
        }

        if (!ctx.User.TryGetUserId(out var userId))
        {
            await Reject(ctx, "missing sub claim");
            return;
        }

        var tvClaim = ctx.User.FindFirstValue(JwtTokenService.TokenVersionClaim);
        if (!int.TryParse(tvClaim, out var tokenVersion))
        {
            await Reject(ctx, "missing or invalid tv claim");
            return;
        }

        var currentVersion = await cache.GetCurrentAsync(userId, db, ctx.RequestAborted);
        if (currentVersion is null)
        {
            await Reject(ctx, $"user {userId} not found or inactive");
            return;
        }

        if (currentVersion.Value != tokenVersion)
        {
            await Reject(ctx, $"token version mismatch for user {userId} (token={tokenVersion}, current={currentVersion})");
            return;
        }

        await _next(ctx);
    }

    private async Task Reject(HttpContext ctx, string reason)
    {
        _logger.LogInformation("Rejecting authenticated request: {Reason}", reason);
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync("""{"error":"unauthorized"}""");
    }
}

public static class TokenVersionMiddlewareExtensions
{
    public static IApplicationBuilder UseTokenVersionValidation(this IApplicationBuilder app)
        => app.UseMiddleware<TokenVersionValidationMiddleware>();
}
