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
    public void Committed_query_counts_only_sales_that_have_not_taken_their_stock()
    {
        using var db = NewContext();

        var sql = SpareSaleService.CommittedQuery(db, [1L], excludeSaleId: 7).ToQueryString();

        // The stock axis is sold_at, not the status: a sale marked sold has already drawn its units out
        // of the balance and counting it again would hide stock that is genuinely on the shelf, while a
        // sale invoiced but not yet marked still owes the warehouse those units and must count.
        sql.Should().Contain("sold_at");
        // Cancelled sales never ship, so they claim nothing either way.
        sql.Should().Contain("Cancelled");
        sql.Should().Contain("is_deleted");
        // The sale being edited must not count against itself.
        sql.Should().Contain("7");
    }

    /// <summary>The status is deliberately NOT what decides a claim any more. An invoiced sale whose
    /// goods have not been handed over still holds its units, and pinning the filter back to
    /// Status == Pending would quietly release them to the next sale.</summary>
    [Fact]
    public void Committed_query_does_not_filter_on_pending_status()
    {
        using var db = NewContext();

        var sql = SpareSaleService.CommittedQuery(db, [1L], excludeSaleId: 7).ToQueryString();

        sql.Should().NotContain("Pending");
    }
}
