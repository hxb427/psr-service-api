using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.SpareSales;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The sales register is read per region, and the question is nearly always about more than one
/// state at a time — "what went into Tamil Nadu and Karnataka this month" was two passes down a
/// single-state drop-down and a merge done in somebody's head.
///
/// Translated to SQL here rather than counted over a seeded database, for the same reason
/// RegisterDispatchFilterTests is: the filter composes into the register's own joined, paged query, and a
/// predicate the provider cannot translate fails in front of whoever is searching rather than at build
/// time. The code parsing is arithmetic on strings and is checked directly.</summary>
public class SaleStateFilterTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    private static IQueryable<SpareSale> Sales(AppDbContext db) =>
        db.SpareSales.AsNoTracking().Where(s => !s.IsDeleted);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    [InlineData(", ,")]
    [InlineData("Kerala")]          // a name is not a code; nothing here guesses one
    public void No_usable_code_leaves_the_query_alone(string? states)
    {
        using var db = NewContext();

        SpareSalesEndpoints.ApplyStateFilter(Sales(db), states).ToQueryString()
            .Should().Be(Sales(db).ToQueryString(), "a stray comma must not empty the register");
    }

    [Fact]
    public void One_state_narrows_on_the_sale_s_own_state_column()
    {
        using var db = NewContext();

        var sql = SpareSalesEndpoints.ApplyStateFilter(Sales(db), "32").ToQueryString();

        // The state recorded on the sale, not the party master's: that is what the counter entered and
        // what the documents were raised on.
        sql.Should().Contain("party_state_code");
        sql.Should().NotContain("dealers");
    }

    [Fact]
    public void Several_states_are_asked_for_together()
    {
        using var db = NewContext();

        var sql = SpareSalesEndpoints.ApplyStateFilter(Sales(db), "33,29").ToQueryString();

        // One pass over both, which is the whole point — IN rather than a query per state.
        sql.Should().Contain("IN (");
        sql.Should().Contain("33").And.Contain("29");
    }

    /// <summary>A sale with nothing recorded is outside the filter rather than swept into it. Guessing
    /// would put a walk-in nobody asked about into whichever state was being looked at.</summary>
    [Fact]
    public void A_sale_with_no_state_is_not_matched()
    {
        using var db = NewContext();

        SpareSalesEndpoints.ApplyStateFilter(Sales(db), "32").ToQueryString()
            .Should().Contain("IS NOT NULL");
    }

    [Theory]
    [InlineData("7", "07")]              // a one-digit code from an older master
    [InlineData(" 32 ", "32")]           // spaces around the pick
    [InlineData("32 - Kerala", "32")]    // the display text pasted into the box
    public void A_code_is_normalised_to_the_two_digit_form(string given, string expected)
        => SpareSalesEndpoints.ParseStateCodes(given).Should().Equal(expected);

    [Fact]
    public void The_same_state_twice_is_asked_for_once()
        => SpareSalesEndpoints.ParseStateCodes("32,32,07").Should().Equal("32", "07");

    [Fact]
    public void Junk_between_codes_is_dropped_and_the_rest_still_asked_for()
        => SpareSalesEndpoints.ParseStateCodes("33,,Kerala,29").Should().Equal("33", "29");
}
