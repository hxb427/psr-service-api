using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Options;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Settings;
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

    // ------------------------------------------------------- per-client lifetime (field portal)
    // The Android field portal gets a week per login and is out of the nightly cutoff; the desktop
    // keeps its day and its 3am wall. Which one applies is decided by the X-Client-Id header value
    // the caller sent, because the risk the cutoff answers to belongs to the machine, not the account.

    private static User Alice => new() { Id = 1, Username = "alice" };

    [Fact]
    public void Field_portal_token_lasts_a_week_and_ignores_the_nightly_cutoff()
    {
        var svc = NewService(Cutoff());
        var (_, expires) = svc.Issue(Alice, new[] { "technician" }, ClientKinds.FieldPortal);

        // A week out, so necessarily past tonight's cutoff — the point of the whole change.
        expires.Should().BeAfter(svc.NextCutoffUtc(DateTime.UtcNow));
        expires.Should().BeCloseTo(DateTime.UtcNow.AddHours(168), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Desktop_token_keeps_its_cutoff_when_the_field_portal_lifetime_is_a_week()
    {
        // Both clients, one options object: raising the phone's lifetime must not reach across and
        // leave a shared bench machine signed in overnight.
        var svc = NewService(Cutoff());
        var (_, expires) = svc.Issue(Alice, new[] { "admin" }, ClientKinds.Wpf);

        expires.Should().BeOnOrBefore(svc.NextCutoffUtc(DateTime.UtcNow));
    }

    [Theory]
    [InlineData(null)]          // desktop builds that predate the header
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-client")]
    public void Unknown_client_ids_get_the_shorter_desktop_session(string? clientId)
    {
        // Same rule as the version gate: an id nobody recognises lands on the stricter treatment, so
        // dropping the header is not a way to buy a week-long token.
        var svc = NewService(Cutoff());
        var (_, expires) = svc.Issue(Alice, new[] { "admin" }, clientId);

        expires.Should().BeOnOrBefore(svc.NextCutoffUtc(DateTime.UtcNow));
    }

    [Fact]
    public void Field_portal_client_id_is_matched_regardless_of_case_or_padding()
    {
        var svc = NewService(Cutoff());
        var (_, expires) = svc.Issue(Alice, new[] { "technician" }, "  Field-Portal  ");

        expires.Should().BeAfter(svc.NextCutoffUtc(DateTime.UtcNow));
    }

    [Fact]
    public void Field_portal_can_be_put_back_under_the_cutoff_by_configuration()
    {
        var opts = Cutoff();
        opts.FieldPortalDailyCutoff = true;

        var svc = NewService(opts);
        var (_, expires) = svc.Issue(Alice, new[] { "technician" }, ClientKinds.FieldPortal);

        // Which also makes the week unreachable — 3am always arrives first. That is the documented
        // consequence of the switch, not a bug in it.
        expires.Should().BeOnOrBefore(svc.NextCutoffUtc(DateTime.UtcNow));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_throws_if_a_configured_lifetime_is_not_positive(int hours)
    {
        // A non-positive lifetime mints already-expired tokens: login succeeds and every call 401s.
        var byDesktop = Cutoff(expiryHours: hours);
        var act = () => NewService(byDesktop);
        act.Should().Throw<InvalidOperationException>();

        var byFieldPortal = Cutoff();
        byFieldPortal.FieldPortalExpiryHours = hours;
        var act2 = () => NewService(byFieldPortal);
        act2.Should().Throw<InvalidOperationException>();
    }
}
