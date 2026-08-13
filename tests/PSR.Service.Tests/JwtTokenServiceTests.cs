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

    [Fact]
    public void Constructor_throws_if_cutoff_hour_is_not_an_hour_of_the_day()
    {
        var act = () => NewService(Cutoff(hour: 24));
        act.Should().Throw<InvalidOperationException>();
    }

    // ---------------------------------------------------------------- nightly cutoff
    // Every session ends at 03:00 local (IST, UTC+5:30 => 21:30 UTC the previous day) so that it
    // dies overnight rather than at whatever time the user happened to sign in.

    private static JwtOptions Cutoff(int hour = 3, int expiryHours = 24) => new()
    {
        Issuer = "test-iss",
        Audience = "test-aud",
        Signing = "TestSigningKey_NotForProductionUse_AtLeast32Chars",
        ExpiryHours = expiryHours,
        DailyCutoffLocalHour = hour,
        LocalUtcOffsetHours = 5.5,
    };

    [Theory]
    // Signed in during the working day -> lasts the whole day, ends at 03:00 IST the next morning.
    [InlineData("2026-08-13T03:30:00Z", "2026-08-13T21:30:00Z")]   // 09:00 IST Thu -> 03:00 IST Fri
    [InlineData("2026-08-13T13:00:00Z", "2026-08-13T21:30:00Z")]   // 18:30 IST Thu -> 03:00 IST Fri
    // Just after the cutoff -> nearly a full day, ending at the NEXT night's cutoff. With a 24h
    // ceiling the next cutoff is always less than 24h away, so it is always the cutoff that bites.
    [InlineData("2026-08-13T21:31:00Z", "2026-08-14T21:30:00Z")]   // 03:01 IST Fri -> 03:00 IST Sat
    // Just before the cutoff -> a short session that ends on the same wall-clock boundary.
    [InlineData("2026-08-13T21:29:00Z", "2026-08-13T21:30:00Z")]   // 02:59 IST Fri -> 03:00 IST Fri
    public void Token_never_outlives_the_nightly_cutoff(string nowIso, string expectedIso)
    {
        var svc = NewService(Cutoff());
        var now = DateTime.Parse(nowIso, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                                              | System.Globalization.DateTimeStyles.AssumeUniversal);

        var cutoff = svc.NextCutoffUtc(now);
        var expected = DateTime.Parse(expectedIso, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                                                       | System.Globalization.DateTimeStyles.AssumeUniversal);

        // What Issue() applies: whichever of the two comes first.
        var effective = cutoff < now.AddHours(24) ? cutoff : now.AddHours(24);
        effective.Should().Be(expected);
    }

    [Fact]
    public void Cutoff_is_always_in_the_future()
    {
        var svc = NewService(Cutoff());
        // Walk a full day in ten-minute steps: no instant may map to a cutoff already past, which is
        // what would issue an already-expired token.
        for (var m = 0; m < 24 * 60; m += 10)
        {
            var now = new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc).AddMinutes(m);
            svc.NextCutoffUtc(now).Should().BeAfter(now, $"cutoff must be ahead of {now:u}");
        }
    }

    [Fact]
    public void Issued_expiry_respects_the_cutoff()
    {
        var svc = NewService(Cutoff());
        var (_, expires) = svc.Issue(new User { Id = 1, Username = "alice" }, new[] { "admin" });

        expires.Should().BeAfter(DateTime.UtcNow);
        expires.Should().BeOnOrBefore(svc.NextCutoffUtc(DateTime.UtcNow));
        expires.Should().BeOnOrBefore(DateTime.UtcNow.AddHours(24));
    }
}
