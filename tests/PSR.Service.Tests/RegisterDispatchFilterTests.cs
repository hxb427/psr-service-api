using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Reports;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The register asks two independent questions about a job: where it is in the workflow, and
/// whether it has left the building. They used to be one filter, which made "everything waiting to go
/// out" and "everything in service" alternatives — so the desk could not ask which of the jobs still
/// here are at which stage, which is the question it actually has.
///
/// Translated to SQL here rather than counted over a seeded database: the filter composes into the
/// register's own joined, paged query, and a predicate the provider cannot translate fails in front of
/// whoever is searching, not at build time.</summary>
public class RegisterDispatchFilterTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    private static IQueryable<ServiceJob> Jobs(AppDbContext db) =>
        db.Services.AsNoTracking().Where(s => !s.IsDeleted);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("anything else")]
    public void No_dispatch_filter_leaves_the_query_alone(string? dispatch)
    {
        using var db = NewContext();

        var filtered = ReportsEndpoints.ApplyDispatchFilter(Jobs(db), dispatch);

        filtered.ToQueryString().Should().Be(Jobs(db).ToQueryString());
    }

    /// <summary>Still here = not dispatched, and not closed out some other way. A stocked, replaced or
    /// written-off job is never going out, so it is in neither half of the toggle.</summary>
    [Fact]
    public void Pending_excludes_dispatched_and_every_closed_status()
    {
        using var db = NewContext();

        var sql = ReportsEndpoints.ApplyDispatchFilter(Jobs(db), "pending").ToQueryString();

        // The four != collapse into one NOT IN, which is the provider's business — what matters is that
        // the narrowing is on the status column and names every status that is not still here.
        sql.Should().Contain("service_status").And.Contain("NOT IN");
        foreach (var closed in new[] { nameof(ServiceStatus.Dispatched), nameof(ServiceStatus.Stocked),
                                       nameof(ServiceStatus.Replaced), nameof(ServiceStatus.TotalLoss) })
            sql.Should().Contain(closed, $"a {closed} job is not waiting to go out");
    }

    /// <summary>The whole point of the split: a job in service, acknowledged, assigned or merely booked
    /// in is still here, so nothing in the open run is excluded by the pending half.</summary>
    [Theory]
    [InlineData(nameof(ServiceStatus.Inward))]
    [InlineData(nameof(ServiceStatus.Assigned))]
    [InlineData(nameof(ServiceStatus.Acknowledged))]
    [InlineData(nameof(ServiceStatus.InService))]
    [InlineData(nameof(ServiceStatus.Completed))]
    [InlineData(nameof(ServiceStatus.ReplacementApprovalPending))]
    public void Pending_keeps_every_open_status(string open)
    {
        using var db = NewContext();

        ReportsEndpoints.ApplyDispatchFilter(Jobs(db), "pending").ToQueryString()
            .Should().NotContain(open, "an open job has not gone anywhere yet");
    }

    [Fact]
    public void Dispatched_narrows_to_the_jobs_that_went_out()
    {
        using var db = NewContext();

        var sql = ReportsEndpoints.ApplyDispatchFilter(Jobs(db), "dispatched").ToQueryString();

        sql.Should().Contain(nameof(ServiceStatus.Dispatched));
        sql.Should().NotContain(nameof(ServiceStatus.Stocked));
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData(" Pending ")]
    [InlineData("Dispatched")]
    public void The_token_is_read_case_and_space_insensitively(string dispatch)
    {
        using var db = NewContext();

        ReportsEndpoints.ApplyDispatchFilter(Jobs(db), dispatch).ToQueryString()
            .Should().NotBe(Jobs(db).ToQueryString());
    }
}
