using FluentAssertions;
using PSR.Service.Api.SpareSales;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Serial numbers on a counter sale, which are asked for at ONE moment: mark-sold.
///
/// A sale is written as quantities — when it is entered nobody knows which boxes will be pulled — so
/// the numbers arrive with the hand-over, against the quantities already agreed. That timing is what
/// these tests are really about: there is no second moment at which the numbers can be wrong, and no
/// window in which two unsold sales hold the same number, so the cross-sale conflict machinery that
/// used to be here is gone rather than merely unused.</summary>
public class SpareSaleSerialTests
{
    // ---------------------------------------------------------------- normalising what was typed

    /// <summary>A barcode scanner sends a trailing newline and the capture box keeps an empty last
    /// line, so blank entries arrive on every other hand-over. Dropping them is what makes "5 typed"
    /// mean five units.</summary>
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
    public void A_part_answered_with_one_serial_per_unit_is_not_a_problem()
        => SpareSalesEndpoints.SerialCountProblems([("950000220", 5, 5)]).Should().BeEmpty();

    /// <summary>The boundary is exactness, in both directions. Short means a box was never read; OVER
    /// means a prompt sized to 5 came back with 6, so one box in the pile is not on this sale.
    ///
    /// Zero is the case that matters most and the one that decides the whole feature: a tracked line
    /// answered with nothing must be refused, or a sale of tracked stock could be handed over
    /// untracked — which is what used to happen to every sale written before serials were asked
    /// for.</summary>
    [Theory]
    [InlineData(5, 5, false)]
    [InlineData(5, 4, true)]
    [InlineData(5, 0, true)]
    [InlineData(5, 6, true)]
    [InlineData(1, 0, true)]
    public void The_count_has_to_match_the_quantity_exactly(int qty, int serials, bool expectProblem)
    {
        var problems = SpareSalesEndpoints.SerialCountProblems([("950000220", qty, serials)]);

        if (expectProblem) problems.Should().ContainSingle()
            .Which.Should().Contain($"{qty} being sold, {serials} serial number(s)");
        else problems.Should().BeEmpty();
    }

    /// <summary>Every short part at once, not the first — the point of the message is to be told once
    /// what to go and read off the boxes.</summary>
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

    /// <summary>Nothing tracked on the sale, nothing to answer for. Marking such a sale sold stays the
    /// single click it always was, which is most sales.</summary>
    [Fact]
    public void A_sale_with_no_tracked_part_has_nothing_to_answer_for()
        => SpareSalesEndpoints.SerialCountProblems([]).Should().BeEmpty();
}
