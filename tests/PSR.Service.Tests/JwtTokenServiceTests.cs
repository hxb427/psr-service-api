using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Options;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data.Entities;
using Xunit;

namespace PSR.Service.Tests;

public class JwtTokenServiceTests
{
    private static JwtTokenService NewService(JwtOptions? overrides = null)
    {
        var opts = overrides ?? new JwtOptions
        {
            Issuer = "test-iss",
            Audience = "test-aud",
            Signing = "TestSigningKey_NotForProductionUse_AtLeast32Chars",
            ExpiryHours = 1,
        };
        return new JwtTokenService(Options.Create(opts));
    }

    [Fact]
    public void Issued_token_contains_expected_claims()
    {
        var svc = NewService();
        var user = new User { Id = 42, Username = "alice", TokenVersion = 7 };
        var (token, expires) = svc.Issue(user, new[] { "admin", "manager" });

        token.Should().NotBeNullOrEmpty();
        expires.Should().BeAfter(DateTime.UtcNow);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Subject.Should().Be("42");
        jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.UniqueName).Value.Should().Be("alice");
        jwt.Claims.First(c => c.Type == JwtTokenService.TokenVersionClaim).Value.Should().Be("7");
        jwt.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value)
            .Should().BeEquivalentTo(new[] { "admin", "manager" });
        jwt.Issuer.Should().Be("test-iss");
        jwt.Audiences.Should().Contain("test-aud");
    }

    [Fact]
    public void Constructor_throws_if_signing_key_is_too_short()
    {
        var act = () => NewService(new JwtOptions { Signing = "tooshort" });
        act.Should().Throw<InvalidOperationException>();
    }
}
