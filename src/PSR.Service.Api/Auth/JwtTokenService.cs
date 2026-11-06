using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Settings;

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

        // Both lifetimes must be positive: a zero or negative value mints tokens that are already
        // expired, which presents as "login succeeds, every call 401s" — worth failing at startup for.
        if (_options.ExpiryHours <= 0)
            throw new InvalidOperationException("Jwt:ExpiryHours must be greater than zero.");

        if (_options.FieldPortalExpiryHours <= 0)
            throw new InvalidOperationException("Jwt:FieldPortalExpiryHours must be greater than zero.");

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

    /// <summary>Mints a token for <paramref name="user"/>.
    ///
    /// <paramref name="clientId"/> is the caller's <see cref="ClientVersionGate.ClientIdHeaderName"/>
    /// header and decides the lifetime, because how long a session may safely live is a property of
    /// the machine it lives on rather than of the account. Missing, blank or unrecognised ids get the
    /// desktop's shorter day plus the nightly cutoff — the same "unknown id gets the stricter
    /// treatment" rule the version gate applies, so a caller cannot talk its way into a longer
    /// session by omitting a header.</summary>
    public (string Token, DateTime ExpiresAt) Issue(
        User user, IReadOnlyCollection<string> roles, string? clientId = null)
    {
        var now = DateTime.UtcNow;
        var isFieldPortal = ClientKinds.Normalize(clientId) == ClientKinds.FieldPortal;

        var expires = now.AddHours(
            isFieldPortal ? _options.FieldPortalExpiryHours : _options.ExpiryHours);

        // Whichever comes first. Signing in shortly before the cutoff therefore buys a short session
        // that ends at the cutoff like everyone else's — the boundary is a wall-clock time, not a
        // per-user countdown, which is exactly what keeps it out of working hours.
        //
        // The field portal is out of this by default: clamping a week to the next 3am would make the
        // longer lifetime unreachable, which is the whole reason it is configured separately.
        if (!isFieldPortal || _options.FieldPortalDailyCutoff)
        {
            var cutoff = NextCutoffUtc(now);
            if (cutoff < expires) expires = cutoff;
        }

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
