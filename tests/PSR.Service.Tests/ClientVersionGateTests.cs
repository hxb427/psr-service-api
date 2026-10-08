using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Settings;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The gate decides, per request, which floor applies and whether the caller clears it.
/// Both halves are worth pinning: picking the wrong settings row silently applies the desktop floor
/// to a phone (or the reverse), and a parse slip either locks out a good build or lets an old one in.</summary>
public class ClientVersionGateTests
{
    // ----- Which floor applies -----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_client_id_falls_back_to_the_desktop_floor(string? clientId)
    {
        // Every WPF build already in the field predates X-Client-Id. If a missing header resolved to
        // anything else, shipping this change would move the whole desktop estate onto a floor nobody
        // set for it.
        ClientVersionGate.SettingKeyFor(clientId).Should().Be(SettingKeys.MinClientVersion);
    }

    [Theory]
    [InlineData("field-portal")]
    [InlineData("FIELD-PORTAL")]
    [InlineData("  Field-Portal  ")]
    public void Field_portal_resolves_to_its_own_floor(string clientId)
        => ClientVersionGate.SettingKeyFor(clientId).Should().Be(SettingKeys.MinFieldPortalVersion);

    [Fact]
    public void Wpf_resolves_to_the_desktop_floor()
        => ClientVersionGate.SettingKeyFor(ClientKinds.Wpf).Should().Be(SettingKeys.MinClientVersion);

    [Fact]
    public void Unknown_client_id_lands_on_the_desktop_floor_rather_than_slipping_through()
    {
        // An id nobody recognises must not be a way around the gate: it gets the strictest floor we
        // keep, not a pass.
        ClientVersionGate.SettingKeyFor("definitely-not-a-client")
            .Should().Be(SettingKeys.MinClientVersion);
    }

    [Fact]
    public void The_two_clients_do_not_share_a_floor()
    {
        // The whole point of the change: raising the desktop floor must not reach across and brick
        // a field portal sitting on an unrelated version line.
        ClientVersionGate.SettingKeyFor(ClientKinds.Wpf)
            .Should().NotBe(ClientVersionGate.SettingKeyFor(ClientKinds.FieldPortal));
    }

    [Fact]
    public void Every_known_client_has_a_cache_key_of_its_own()
    {
        var keys = ClientKinds.All
            .Select(c => ClientVersionGate.CacheKeyFor(ClientVersionGate.SettingKeyFor(c)))
            .ToList();
        keys.Should().OnlyHaveUniqueItems();
    }

    // ----- Version parsing -----

    [Theory]
    [InlineData("1.2.0", 1, 2, 0)]
    [InlineData("v1.2.0", 1, 2, 0)]
    [InlineData("1.2.0-beta+abc", 1, 2, 0)]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("2", 2, 0, 0)]
    [InlineData("1.2", 1, 2, 0)]
    public void Parses_the_shapes_releases_actually_carry(string raw, int major, int minor, int build)
    {
        ClientVersionGate.TryParse(raw, out var v).Should().BeTrue();
        v.Should().Be(new Version(major, minor, build));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("1..2")]
    public void Rejects_what_it_cannot_read(string? raw)
    {
        // The caller treats a false here as 0.0.0, so a garbled header is blocked rather than trusted.
        ClientVersionGate.TryParse(raw, out var v).Should().BeFalse();
        v.Should().Be(new Version(0, 0, 0));
    }

    [Fact]
    public void A_dev_build_is_not_below_a_floor_of_exactly_zero()
    {
        // "0.0.0-dev" reads as 0.0.0, and the gate skips entirely when the floor is 0.0.0 — so local
        // builds keep working until someone deliberately sets a floor.
        ClientVersionGate.TryParse("0.0.0-dev", out var dev).Should().BeTrue();
        dev.Should().Be(new Version(0, 0, 0));
    }

    [Fact]
    public void An_older_build_sorts_below_the_floor_it_must_clear()
    {
        ClientVersionGate.TryParse("1.2.0", out var floor).Should().BeTrue();
        ClientVersionGate.TryParse("1.1.9", out var old).Should().BeTrue();
        ClientVersionGate.TryParse("1.2.1", out var newer).Should().BeTrue();

        old.Should().BeLessThan(floor);
        newer.Should().BeGreaterThan(floor);
    }
}
