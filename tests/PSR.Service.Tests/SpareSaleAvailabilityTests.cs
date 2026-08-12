using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.SpareSales;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Availability is what stops two counter sales being written against the same units, and the
/// order is pay-then-invoice — so a wrong figure here means money taken for goods that are not there.
/// The subtraction itself is arithmetic and tested directly; the committed-quantity query is checked by
/// forcing the provider to translate it, which is where a GroupBy or a Contains would fail only against
/// the real database.</summary>
public class SpareSaleAvailabilityTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    [Theory]
    [InlineData(10, 0, 10)]   // nothing else pending — available is the shelf
    [InlineData(10, 4, 6)]    // four promised elsewhere
    [InlineData(10, 10, 0)]   // all spoken for
    [InlineData(3, 5, -2)]    // stock adjusted down under sales already entered: report the shortfall
    public void Available_is_on_hand_less_committed(int onHand, int committed, int expected)
        => new PartAvailability(1, onHand, committed).Available.Should().Be(expected);

    [Fact]
    public void Committed_query_translates_to_sql()
    {
        using var db = NewContext();

        var sql = SpareSaleService.CommittedQuery(db, [1L, 2L], excludeSaleId: 7).ToQueryString();

        // Summed by the database, not by pulling every pending sale line back to add up here.
        sql.Should().Contain("GROUP BY");
        sql.Should().Contain("SUM(");
        sql.Should().Contain("JOIN");
    }

    [Fact]
    public void Committed_query_counts_only_live_pending_sales()
    {
        using var db = NewContext();

        var sql = SpareSaleService.CommittedQuery(db, [1L], excludeSaleId: 7).ToQueryString();

        // An invoiced sale has already taken its stock and a cancelled one never will, so counting
        // either would hide units that are genuinely on the shelf.
        sql.Should().Contain("Pending");
        sql.Should().Contain("is_deleted");
        // The sale being edited must not count against itself.
        sql.Should().Contain("7");
    }
}
