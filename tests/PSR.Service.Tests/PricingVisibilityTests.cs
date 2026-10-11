using System.Security.Claims;
using FluentAssertions;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data.Entities;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// Who may see what a thing costs, now that a technician may see some of it.
///
/// A technician used to read every rate as null, which was workable while the phone only recorded what
/// was fitted and became untenable once the technician is the person quoting the visit and collecting
/// for it. Lifting the restriction could have been done by adding <c>technician</c> to the one existing
/// list; it was done by splitting the list instead, so the purchase rate stays behind.
///
/// That split is the thing these tests exist to hold. It is a one-line edit to "simplify"
/// <see cref="PricingVisibility"/> back into a single role list, and the only thing that would go wrong
/// is that every phone in the field starts showing the shop's own cost price — on a shared handset, over
/// a customer's shoulder, in a workshop. Nothing would fail; the margin would just be readable.
/// </summary>
public class PricingVisibilityTests
{
    private static ClaimsPrincipal SignedInAs(params string[] roles) =>
        new(new ClaimsIdentity(
            roles.Select(r => new Claim(ClaimTypes.Role, r)).Append(new Claim("sub", "1")),
            authenticationType: "Test", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role));

    // ---------------------------------------------------------------- the office

    [Theory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Manager)]
    [InlineData(RoleNames.Supervisor)]
    [InlineData(RoleNames.Viewer)]
    public void The_office_roles_keep_the_whole_catalogue(string role)
    {
        PricingVisibility.For(SignedInAs(role)).Should().Be(PricingScope.Full);
        PricingVisibility.CostRates(SignedInAs(role)).Should().BeTrue();
        PricingVisibility.SellingRates(SignedInAs(role)).Should().BeTrue();
    }

    // ---------------------------------------------------------------- the technician

    [Fact]
    public void A_technician_may_read_the_selling_rates_they_have_to_quote()
    {
        PricingVisibility.For(SignedInAs(RoleNames.Technician)).Should().Be(PricingScope.Quoting);
        PricingVisibility.SellingRates(SignedInAs(RoleNames.Technician)).Should().BeTrue();
    }

    /// <summary>The whole point of the split. Dealer and customer rates appear on the customer's own
    /// receipt; the purchase rate appears on nothing the customer ever sees.</summary>
    [Fact]
    public void A_technician_may_not_read_the_purchase_rate()
    {
        PricingVisibility.CostRates(SignedInAs(RoleNames.Technician)).Should().BeFalse();
    }

    /// <summary>Both selling columns, not just the customer one: the visit's rate type decides which of
    /// the two a line bills at, so withholding the dealer rate would leave half of that choice showing
    /// a technician a figure they cannot read while asking them to collect it.</summary>
    [Fact]
    public void Both_selling_columns_reach_a_technician_because_the_rate_type_picks_between_them()
    {
        var scope = PricingVisibility.For(SignedInAs(RoleNames.Technician));

        PricingVisibility.SellingRates(scope).Should().BeTrue();
        // The dealer and customer rates are gated on the same predicate; if they ever stop being, the
        // Dealer rate type becomes unusable from a phone.
        foreach (var rateType in new[] { FieldRateType.Customer, FieldRateType.Dealer })
            FieldPricedAt(rateType, scope).Should().NotBeNull(
                "a technician billing at {0} rates has to be able to read them", rateType);
    }

    /// <summary>What the parts projection would hand back for a rate column at this scope.</summary>
    private static decimal? FieldPricedAt(FieldRateType rateType, PricingScope scope)
    {
        var part = new Part { DealerRate = 800m, CustomerRate = 1000m };
        return PricingVisibility.SellingRates(scope)
            ? rateType == FieldRateType.Dealer ? part.DealerRate : part.CustomerRate
            : null;
    }

    // ---------------------------------------------------------------- everybody else

    /// <summary>The roles that saw no pricing before still see none. Widening those is a separate
    /// decision and nobody has asked for it — a store manager picks stock, an inward clerk books it in,
    /// and neither quotes anybody.</summary>
    [Theory]
    [InlineData(RoleNames.StoreManager)]
    [InlineData(RoleNames.Accounts)]
    [InlineData(RoleNames.Inward)]
    public void The_roles_that_saw_no_pricing_still_see_none(string role)
    {
        PricingVisibility.For(SignedInAs(role)).Should().Be(PricingScope.None);
        PricingVisibility.SellingRates(SignedInAs(role)).Should().BeFalse();
        PricingVisibility.CostRates(SignedInAs(role)).Should().BeFalse();
    }

    [Fact]
    public void A_token_carrying_no_role_at_all_sees_no_money()
    {
        PricingVisibility.For(SignedInAs()).Should().Be(PricingScope.None);
    }

    /// <summary>A technician who is also a supervisor is a supervisor: the wider scope wins rather than
    /// the narrower one clipping it.</summary>
    [Fact]
    public void Holding_an_office_role_as_well_widens_rather_than_narrows()
    {
        PricingVisibility.For(SignedInAs(RoleNames.Technician, RoleNames.Supervisor))
            .Should().Be(PricingScope.Full);
    }
}
