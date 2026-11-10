using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.SpareSales;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Serial numbers on a counter sale: the rules that decide whether a sale of serial-tracked
/// stock may be written, and whether it may be handed over.
///
/// A sale names its units when it is written and tracks them when it is marked sold, so there are two
/// separate moments at which the numbers can be wrong, and the pair of them is what is pinned here.</summary>
public class SpareSaleSerialTests
{
    // ---------------------------------------------------------------- normalising what was typed

    /// <summary>A barcode scanner sends a trailing newline and the form keeps an empty row at the
    /// bottom, so blank entries arrive on every other sale. Dropping them is what makes "5 typed" mean
    /// five units.</summary>
    [Fact]
    public void Blank_and_padded_entries_are_dropped_and_trimmed()
    {
        var result = SpareSaleService.Normalize(["  PSR-1 ", "", "   ", "PSR-2", null!]);

        result.Should().Equal("PSR-1", "PSR-2");
    }

    /// <summary>Case is NOT folded. The serial is stored as it is printed on the box — every comparison
    /// around it is case-insensitive instead, so "psr-1" is still found to be the same unit as "PSR-1"
    /// without the ledger having to shout it.</summary>
    [Fact]
    public void Case_is_kept_as_it_was_typed()
        => SpareSaleService.Normalize(["psr-1"]).Should().Equal("psr-1");

    [Fact]
    public void Nothing_in_gives_nothing_out()
        => SpareSaleService.Normalize(null).Should().BeEmpty();

    // ---------------------------------------------------------------- one serial per unit

    [Fact]
    public void A_part_with_one_serial_per_unit_is_not_a_problem()
        => SpareSalesEndpoints.SerialCountProblems([("950000220", 5, 5)]).Should().BeEmpty();

    /// <summary>The boundary is exactness, in both directions. Short means a box was never named; OVER
    /// means the quantity was lowered after the units were named, so the counter still thinks it is
    /// selling one it is not — which is the more interesting of the two and used to pass.</summary>
    [Theory]
    [InlineData(5, 5, false)]
    [InlineData(5, 4, true)]
    [InlineData(5, 0, true)]
    [InlineData(5, 6, true)]
    public void The_count_has_to_match_the_quantity_exactly(int qty, int serials, bool expectProblem)
    {
        var problems = SpareSalesEndpoints.SerialCountProblems([("950000220", qty, serials)]);

        if (expectProblem) problems.Should().ContainSingle()
            .Which.Should().Contain($"{qty} being sold, {serials} serial number(s)");
        else problems.Should().BeEmpty();
    }

    /// <summary>Every short part at once, not the first — the point of the message is to be told once
    /// what to go and fix.</summary>
    [Fact]
    public void Every_short_part_is_named()
    {
        var problems = SpareSalesEndpoints.SerialCountProblems(
            [("950000220", 5, 4), ("950000221", 2, 2), ("950000222", 1, 0)]);

        problems.Should().HaveCount(2);
        problems.Should().Contain(p => p.Contains("950000220"));
        problems.Should().Contain(p => p.Contains("950000222"));
        problems.Should().NotContain(p => p.Contains("950000221"));
    }

    // ---------------------------------------------------------------- the cross-sale lookup

    /// <summary>The claim lookup is a three-way join (serial → line → sale) and it runs as the sale form
    /// is typed. An untranslatable one would only blow up on the real database, with a counter waiting
    /// on it, so ToQueryString forces the provider to translate it here instead.</summary>
    [Fact]
    public void The_cross_sale_claim_lookup_translates_to_sql()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);

        var sql = SpareSaleService.ClaimQuery(db, [7L]).ToQueryString();

        sql.Should().Contain("spare_sale_line_serials");
        sql.Should().Contain("spare_sale_lines");
        sql.Should().Contain("spare_sales");
    }

    /// <summary>What makes a claim one that is in the way, pinned as SQL. Each of these three is a
    /// different sale that must NOT hold a serial against the next one: deleted and cancelled sales
    /// will never be marked sold, and a sold one's units are in the serial ledger by now and are
    /// reported from there instead — with the state the unit is actually in.</summary>
    [Theory]
    [InlineData("is_deleted")]
    [InlineData("sold_at")]
    [InlineData("Cancelled")]
    public void Only_a_live_unsold_sale_holds_a_claim(string expected)
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);

        SpareSaleService.ClaimQuery(db, [7L]).ToQueryString().Should().Contain(expected);
    }

    /// <summary>Asking about nothing asks the database nothing. The form calls this on its way to
    /// saving, including for a sale with no tracked item on it at all.</summary>
    [Fact]
    public async Task No_lines_means_no_conflicts_and_no_query()
    {
        await using var db = new DesignTimeDbContextFactory().CreateDbContext([]);

        var conflicts = await new SpareSaleService(db)
            .FindSerialConflictsAsync(null, [], CancellationToken.None);

        conflicts.Should().BeEmpty();
    }
}
