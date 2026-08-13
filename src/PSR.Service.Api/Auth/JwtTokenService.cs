using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Auth;

public class JwtTokenService
{
    public const string TokenVersionClaim = "tv";

    private readonly JwtOptions _options;
    private readonly SigningCredentials _signingCredentials;

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.Signing) || _options.Signing.Length < 32)
            throw new InvalidOperationException(
                "Jwt:Signing must be configured and at least 32 characters long.");

        if (_options.DailyCutoffLocalHour is < 0 or > 23)
            throw new InvalidOperationException(
                "Jwt:DailyCutoffLocalHour must be an hour of the day (0-23).");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Signing));
        _signingCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    /// <summary>The instant every session ends: the next occurrence of the configured local cutoff
    /// hour, expressed in UTC. Exposed so the refresh endpoint and tests can reason about the same
    /// boundary the issuer applies.</summary>
    public DateTime NextCutoffUtc(DateTime nowUtc)
    {
        var offset = TimeSpan.FromHours(_options.LocalUtcOffsetHours);
        var localNow = nowUtc + offset;

        var cutoffLocal = localNow.Date.AddHours(_options.DailyCutoffLocalHour);
        if (cutoffLocal <= localNow)
            cutoffLocal = cutoffLocal.AddDays(1);

        return cutoffLocal - offset;
    }

    public (string Token, DateTime ExpiresAt) Issue(User user, IReadOnlyCollection<string> roles)
    {
        var now = DateTime.UtcNow;

        // Whichever comes first. Signing in shortly before the cutoff therefore buys a short session
        // that ends at the cutoff like everyone else's — the boundary is a wall-clock time, not a
        // per-user countdown, which is exactly what keeps it out of working hours.
        var expires = now.AddHours(_options.ExpiryHours);
        var cutoff = NextCutoffUtc(now);
        if (cutoff < expires) expires = cutoff;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, user.Username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(TokenVersionClaim, user.TokenVersion.ToString()),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var jwt = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: _signingCredentials);

        var token = new JwtSecurityTokenHandler().WriteToken(jwt);
        return (token, expires);
    }
}
