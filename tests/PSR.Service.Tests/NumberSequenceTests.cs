using FluentAssertions;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Document numbers are the one thing in the system that must never repeat: a duplicate sale
/// number or service number is a record that two people can both claim to own. Uniqueness rests
/// entirely on the sequence row being locked with SELECT ... FOR UPDATE, and that lock only holds for
/// the life of the enclosing transaction — so "was there a transaction" is the property worth pinning
/// down, not the formatting of the number.</summary>
public class NumberSequenceTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    [Fact]
    public async Task Refuses_to_issue_a_number_outside_a_transaction()
    {
        using var db = NewContext();
        var seq = new NumberSequenceService(db);

        // No BeginTransactionAsync: the SELECT would autocommit, the row lock would be released before
        // the increment was written, and two concurrent callers would be handed the same number.
        var act = () => seq.NextAsync(SequenceKeys.SpareSale, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*outside a transaction*");
    }

    /// <summary>The guard has to fire before the query runs, not after — otherwise it would only be
    /// reachable against a live database and would never trip in a unit test or on a developer's
    /// machine, which is exactly where a new call site gets written.</summary>
    [Fact]
    public async Task Guard_runs_before_touching_the_database()
    {
        using var db = NewContext();
        var seq = new NumberSequenceService(db);

        var ex = await Record.ExceptionAsync(
            () => seq.NextAsync(SequenceKeys.Service, CancellationToken.None));

        ex.Should().BeOfType<InvalidOperationException>(
            "a connection failure would surface as some provider exception instead");
    }
}
