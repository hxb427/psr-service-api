using FluentAssertions;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Services;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// An advance replacement splits a job in two: the replacement goes out on the customer's job and
/// their own unit stays behind on a retained job of its own. That only stays honest if the retained
/// half can never leave by the customer's door — it is the shop's own stock sitting in the service
/// sections, and both routes that treat a job as a customer's have to refuse it.
///
/// These cover the refusals. The writes need a database, like every other ledger path in this suite.
/// </summary>
public class AdvanceReplacementTests
{
    private const long UserId = 42;

    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);
    private static IAuditService Audit(AppDbContext db) => new AuditService(db);

    private static ServiceJob CompletedJob(JobKind kind = JobKind.Customer) => new()
    {
        Id = 1,
        ServiceNo = "SVC0001",
        SerialNo = "SN1",
        PsCode = "PS-100",
        WarrantyStatus = WarrantyStatus.InWarranty,   // exempt from the outward-reference rule
        ServiceStatus = ServiceStatus.Completed,
        JobKind = kind,
    };

    // ---------------------------------------------------------------- the retained half cannot go out

    /// <summary>The customer was served when the replacement went out. Dispatching the unit they left
    /// behind would hand over a second unit against the same job and take it off the shelf with
    /// nothing recording a sale.</summary>
    [Fact]
    public async Task A_retained_job_cannot_be_dispatched()
    {
        using var db = NewContext();
        var job = CompletedJob(JobKind.SwapRetained);

        var result = await ServicesEndpoints.ApplyDispatchAsync(
            job, new DispatchRequest(), UserId, db, new SerialService(db), Audit(db), null, default);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Invalid);
        result.Error.Should().Contain("cannot be dispatched");
        job.ServiceStatus.Should().Be(ServiceStatus.Completed);
    }

    /// <summary>The guard is on the KIND, not the status, so it has to leave ordinary jobs alone —
    /// including the one the replacement itself went out on, which is an ordinary customer job from
    /// the moment the swap finishes.</summary>
    [Fact]
    public async Task An_ordinary_completed_job_still_dispatches()
    {
        using var db = NewContext();
        var job = CompletedJob();

        var result = await ServicesEndpoints.ApplyDispatchAsync(
            job, new DispatchRequest(), UserId, db, new SerialService(db), Audit(db), null, default);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
        job.ServiceStatus.Should().Be(ServiceStatus.Dispatched);
    }

    /// <summary>A job raised off a faulty field return is also the shop's own unit, but dispatching one
    /// is legitimate — it goes back out to the party on the job. Only the swap-retained kind is barred,
    /// and this pins that the two did not get lumped together.</summary>
    [Fact]
    public async Task A_field_return_job_is_still_dispatchable()
    {
        using var db = NewContext();
        var job = CompletedJob(JobKind.FieldReturn);

        var result = await ServicesEndpoints.ApplyDispatchAsync(
            job, new DispatchRequest(), UserId, db, new SerialService(db), Audit(db), null, default);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
    }

    // ---------------------------------------------------------------- where a swap may be issued from

    [Theory]
    [InlineData(ServiceStatus.Inward)]
    [InlineData(ServiceStatus.Assigned)]
    [InlineData(ServiceStatus.Acknowledged)]
    [InlineData(ServiceStatus.InService)]
    [InlineData(ServiceStatus.Completed)]
    public void A_swap_may_be_issued_from_any_stage_the_machine_is_still_here(ServiceStatus status)
    {
        ServicesEndpoints.SwappableStatuses.Should().Contain(status);
    }

    /// <summary>Once the job has gone out, been shelved or been written off, the machine is not here
    /// to keep — so there is nothing to swap.</summary>
    [Theory]
    [InlineData(ServiceStatus.Dispatched)]
    [InlineData(ServiceStatus.Stocked)]
    [InlineData(ServiceStatus.TotalLoss)]
    [InlineData(ServiceStatus.Replaced)]
    public void A_swap_is_refused_once_the_machine_has_left(ServiceStatus status)
    {
        ServicesEndpoints.SwappableStatuses.Should().NotContain(status);
    }

    // ---------------------------------------------------------------- stocking the retained unit

    /// <summary>The whole point of the retained half: it ends on the shelf. Nothing about its kind
    /// blocks that, which is what the dispatch guard's counterpart has to leave alone.</summary>
    [Fact]
    public async Task A_retained_job_that_is_not_completed_is_refused_by_the_ordinary_status_guard()
    {
        using var db = NewContext();
        var job = CompletedJob(JobKind.SwapRetained);
        job.ServiceStatus = ServiceStatus.InService;

        var result = await ServicesEndpoints.ApplyStockAsync(
            job, null, UserId, db, new StockLedgerService(db), new SerialService(db), Audit(db), null, default);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Invalid);
    }

    /// <summary>A retained unit reaches the shelf under the same rules as anything else: it has to
    /// name a catalogue item, and a tracked one has to name its unit.</summary>
    [Fact]
    public void A_retained_job_stocks_under_the_same_rules_as_any_other()
    {
        var job = CompletedJob(JobKind.SwapRetained);
        var part = new Part { Id = 5, ItemCode = "PS-100", Name = "Control board", IsSerialTracked = true };

        ServicesEndpoints.StockBlockedReason(job, part, job.SerialNo).Should().BeNull();
        ServicesEndpoints.StockBlockedReason(job, part, null).Should().Contain("serial-tracked");
    }
}
