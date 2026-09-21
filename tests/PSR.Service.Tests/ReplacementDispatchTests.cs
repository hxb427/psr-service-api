using System.Security.Claims;
using FluentAssertions;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Services;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Issuing a replacement finishes the work on a job; it does not hand the unit over. The job
/// has to come out of that step in the same state a repaired one does — Completed, queued for dispatch —
/// so that it is billed and dispatched through the ordinary route rather than closed at the counter.
///
/// Checked against the dispatch rule rather than the replace handler, because the rule is what the
/// change is for: the old terminal Replaced status could never be dispatched, which is how a replaced
/// unit ended up with no dispatch step and no turnaround figure.</summary>
public class ReplacementDispatchTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    private static ServiceJob ReplacedJob(ServiceStatus status) => new()
    {
        Id = 1,
        ServiceNo = "SVC0001",
        SerialNo = "SN-IN",
        ServiceStatus = status,
        WarrantyStatus = WarrantyStatus.InWarranty,
        ReplacementSerialNo = "SN-OUT",
        ReplacementPartId = 5,
    };

    [Fact]
    public async Task A_job_whose_unit_was_replaced_dispatches_like_any_other_completed_job()
    {
        using var db = NewContext();
        var job = ReplacedJob(ServiceStatus.Completed);

        var result = await ServicesEndpoints.ApplyDispatchAsync(
            job, new DispatchRequest(), 1, db, new SerialService(db), new AuditService(db), null, default);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
        job.ServiceStatus.Should().Be(ServiceStatus.Dispatched);
    }

    [Fact]
    public async Task The_retired_terminal_status_still_cannot_be_dispatched()
    {
        using var db = NewContext();
        var job = ReplacedJob(ServiceStatus.Replaced);

        var result = await ServicesEndpoints.ApplyDispatchAsync(
            job, new DispatchRequest(), 1, db, new SerialService(db), new AuditService(db), null, default);

        // Rows closed under the old behaviour stay closed — nothing re-opens them.
        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Invalid);
        job.ServiceStatus.Should().Be(ServiceStatus.Replaced);
    }

    [Fact]
    public async Task An_out_of_warranty_replacement_still_needs_a_document_before_it_goes_out()
    {
        using var db = NewContext();
        var job = ReplacedJob(ServiceStatus.Completed);
        job.WarrantyStatus = WarrantyStatus.OutOfWarranty;

        var result = await ServicesEndpoints.ApplyDispatchAsync(
            job, new DispatchRequest(), 1, db, new SerialService(db), new AuditService(db), null, default);

        // The point of routing a replacement back through Completed: it is billable, and the billing
        // rules apply to it unchanged.
        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Invalid);
        result.Error.Should().Contain("no PI, delivery challan or outward reference");
    }
}
