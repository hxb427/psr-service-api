using FluentAssertions;
using PSR.Service.Api.MachineTests;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>A factory record's warranty term is its dealer's, found by matching passtestdata.Customer to
/// the dealer master. Only an exact (normalised) match is applied on its own: the term decides whether a
/// repair is billed, so anything less certain is put to the operator.</summary>
public class DealerWarrantyMatcherTests
{
    private static DealerCandidateDto D(long id, string name, int months = 12, bool active = true) =>
        new(id, name, months, active);

    private static DealerWarrantyMatcher Matcher(long? selected, params DealerCandidateDto[] dealers) =>
        new(dealers, selected);

    [Theory]
    [InlineData("SRI RAM AND CO")]
    [InlineData("M/s. Sri Ram & Co.")]
    [InlineData("  sri   ram and co ")]
    public void Spelling_variants_land_on_the_one_dealer(string customer)
    {
        var match = Matcher(null, D(1, "Sri Ram & Co", 24), D(2, "Lakshmi Agencies")).Resolve(customer);

        match.Kind.Should().Be(DealerMatchKind.Exact);
        match.Dealer!.Id.Should().Be(1);
        match.Months.Should().Be(24);
    }

    [Fact]
    public void Two_live_dealers_on_one_name_are_put_to_the_operator()
    {
        var match = Matcher(null, D(1, "Sri Ram & Co"), D(2, "SRI RAM AND CO")).Resolve("Sri Ram and Co");

        match.Kind.Should().Be(DealerMatchKind.Multiple);
        match.Dealer.Should().BeNull();
        match.Months.Should().BeNull();
        match.Candidates.Select(c => c.Id).Should().BeEquivalentTo([1L, 2L]);
    }

    [Fact]
    public void A_switched_off_duplicate_gives_way_to_the_live_dealer()
    {
        var match = Matcher(null, D(1, "Sri Ram & Co", active: false), D(2, "SRI RAM AND CO", 18)).Resolve("Sri Ram and Co");

        match.Kind.Should().Be(DealerMatchKind.Exact);
        match.Dealer!.Id.Should().Be(2);
    }

    [Fact]
    public void An_inactive_dealer_still_answers_for_the_machines_it_sold()
        => Matcher(null, D(1, "Old Traders", 12, active: false)).Resolve("OLD TRADERS").Kind
            .Should().Be(DealerMatchKind.Exact);

    [Fact]
    public void A_near_spelling_is_offered_not_applied()
    {
        var match = Matcher(null, D(1, "Lakshmi Agencies", 24), D(2, "Unrelated Name")).Resolve("Lakshmi Agencis");

        match.Kind.Should().Be(DealerMatchKind.None);
        match.Dealer.Should().BeNull();
        match.Candidates.Select(c => c.Id).Should().Equal(1L);
    }

    [Fact]
    public void A_name_with_a_place_added_is_offered()
    {
        var match = Matcher(null, D(1, "Sri Ram & Co")).Resolve("Sri Ram & Co Palakkad");

        match.Kind.Should().Be(DealerMatchKind.None);
        match.Candidates.Select(c => c.Id).Should().Equal(1L);
    }

    [Fact]
    public void A_short_name_is_not_found_inside_every_longer_one()
        => Matcher(null, D(1, "ABC")).Resolve("ABC Dairy Farm Kochi").Candidates.Should().BeEmpty();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_customer_is_no_match(string? customer)
    {
        var match = Matcher(null, D(1, "Sri Ram")).Resolve(customer);

        match.Kind.Should().Be(DealerMatchKind.None);
        match.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void The_operators_pick_overrides_the_match()
    {
        var match = Matcher(2, D(1, "Sri Ram", 24), D(2, "Lakshmi Agencies", 6)).Resolve("Sri Ram");

        match.Kind.Should().Be(DealerMatchKind.Selected);
        match.Dealer!.Id.Should().Be(2);
        match.Months.Should().Be(6);
    }

    [Fact]
    public void A_pick_that_no_longer_exists_is_ignored()
        => Matcher(99, D(1, "Sri Ram")).Resolve("Sri Ram").Kind.Should().Be(DealerMatchKind.Exact);

    [Fact]
    public void A_dealer_with_no_term_set_matches_but_gives_no_months()
    {
        var match = Matcher(null, D(1, "Sri Ram", 0)).Resolve("Sri Ram");

        match.Kind.Should().Be(DealerMatchKind.Exact);
        match.Months.Should().BeNull();   // 0 is "not known" — never "zero months, so OUT"
    }

    [Fact]
    public void Verdict_runs_from_the_invoice_date()
    {
        var purchased = DateTime.UtcNow.Date.AddMonths(-6);

        WarrantyVerdict.Compute(purchased, 12).Should().Be(("IN", (DateTime?)purchased.AddMonths(12)));
        WarrantyVerdict.Compute(purchased, 3).Status.Should().Be("OUT");
        WarrantyVerdict.Compute(purchased, null).Should().Be(("UNKNOWN", (DateTime?)null));
        WarrantyVerdict.Compute(null, 12).Status.Should().Be("UNKNOWN");
    }
}
