using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Services;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The bulk workflow routes exist so one lost packet cannot strand a 50-job selection. Two
/// properties have to hold for that to be worth anything: the selection must load in one translatable
/// query, and every action must be safe to replay — a client that retries after a lost response must
/// not be told its jobs failed. Both are checked here against the change tracker, no database needed.</summary>
public class BulkWorkflowTests
{
    private const long TechId = 42;

    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    private static ClaimsPrincipal Technician(long userId = TechId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

    private static ServiceJob AssignedJob(long id = 1) => new()
    {
        Id = id,
        ServiceNo = $"SVC{id:0000}",
        SerialNo = $"SN{id}",
        TechnicianId = TechId,
        ServiceStatus = ServiceStatus.Assigned,
        AckStatus = AckStatus.Pending,
    };

    private static IAuditService Audit(AppDbContext db) => new AuditService(db);

    // ---------------------------------------------------------------- selection loading

    [Fact]
    public void Bulk_selection_query_translates_to_sql()
    {
        using var db = NewContext();

        // ids.Contains(s.Id) hits the EF Core 9 + .NET 10 funcletizer bug, so the predicate is built
        // as an explicit OR-chain. ToQueryString forces the provider to translate it here rather than
        // letting it blow up on the first real bulk call.
        var sql = ServicesEndpoints.BulkIdPredicateQuery(db, [7L, 9L, 11L]).ToQueryString();

        sql.Should().Contain("services");
        sql.Should().Contain("7");
        sql.Should().Contain("9");
        sql.Should().Contain("11");
    }

    [Fact]
    public void Bulk_selection_predicate_matches_exactly_the_requested_ids()
    {
        var predicate = ServicesEndpoints.BulkIdPredicate([2L, 5L]).Compile();

        predicate(AssignedJob(2)).Should().BeTrue();
        predicate(AssignedJob(5)).Should().BeTrue();
        predicate(AssignedJob(3)).Should().BeFalse();
    }

    // ---------------------------------------------------------------- acknowledge

    [Fact]
    public void Acknowledge_moves_an_assigned_job_and_records_history()
    {
        using var db = NewContext();
        var job = AssignedJob();

        var result = ServicesEndpoints.ApplyAcknowledge(job, Technician(), null, TechId, db, Audit(db), null);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
        job.ServiceStatus.Should().Be(ServiceStatus.Acknowledged);
        job.AckStatus.Should().Be(AckStatus.Acknowledged);
        db.ChangeTracker.Entries<ServiceStatusHistory>().Should().HaveCount(1);
    }

    [Fact]
    public void Acknowledge_replayed_on_an_already_acknowledged_job_succeeds_without_a_second_history_row()
    {
        using var db = NewContext();
        var job = AssignedJob();
        var user = Technician();

        ServicesEndpoints.ApplyAcknowledge(job, user, null, TechId, db, Audit(db), null);
        // The retry a client makes after a lost response. It must not report failure, and it must not
        // add a second "Received by technician" line to the job's history.
        var replay = ServicesEndpoints.ApplyAcknowledge(job, user, null, TechId, db, Audit(db), null);

        replay.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
        db.ChangeTracker.Entries<ServiceStatusHistory>().Should().HaveCount(1);
    }

    [Fact]
    public void Acknowledge_stays_idempotent_once_the_job_has_moved_past_acknowledgement()
    {
        using var db = NewContext();
        // AckStatus is set at acknowledgement and never cleared, so it still marks "this was received"
        // after the technician has started work — a replay here is a no-op, not a status complaint.
        var job = AssignedJob();
        job.ServiceStatus = ServiceStatus.InService;
        job.AckStatus = AckStatus.Acknowledged;

        var result = ServicesEndpoints.ApplyAcknowledge(job, Technician(), null, TechId, db, Audit(db), null);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
        db.ChangeTracker.Entries<ServiceStatusHistory>().Should().BeEmpty();
    }

    [Fact]
    public void Acknowledge_is_refused_for_anyone_but_the_assigned_technician()
    {
        using var db = NewContext();

        var result = ServicesEndpoints.ApplyAcknowledge(
            AssignedJob(), Technician(userId: 99), null, 99, db, Audit(db), null);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Forbidden);
    }

    [Fact]
    public void Acknowledge_is_refused_while_the_job_is_still_at_inward()
    {
        using var db = NewContext();
        var job = AssignedJob();
        job.ServiceStatus = ServiceStatus.Inward;

        var result = ServicesEndpoints.ApplyAcknowledge(job, Technician(), null, TechId, db, Audit(db), null);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Invalid);
        result.Error.Should().Contain("Inward");
    }

    // ---------------------------------------------------------------- start

    [Fact]
    public void Start_replayed_on_an_in_service_job_succeeds_without_a_second_history_row()
    {
        using var db = NewContext();
        var job = AssignedJob();
        job.ServiceStatus = ServiceStatus.InService;

        var result = ServicesEndpoints.ApplyStart(job, Technician(), null, TechId, db, Audit(db), null);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
        db.ChangeTracker.Entries<ServiceStatusHistory>().Should().BeEmpty();
    }

    [Fact]
    public void Start_is_refused_before_the_job_has_been_acknowledged()
    {
        using var db = NewContext();

        var result = ServicesEndpoints.ApplyStart(AssignedJob(), Technician(), null, TechId, db, Audit(db), null);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Invalid);
    }

    // ---------------------------------------------------------------- payment

    [Fact]
    public void Payment_replayed_at_the_same_status_adds_no_history_line()
    {
        using var db = NewContext();
        var job = AssignedJob();
        job.ServiceStatus = ServiceStatus.Completed;
        job.PaymentStatus = PaymentStatus.Paid;

        var result = ServicesEndpoints.ApplyPayment(job, PaymentStatus.Paid, TechId, db, Audit(db), null);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
        // Without the idempotency guard this would stamp a meaningless "Paid → Paid" on every replay.
        db.ChangeTracker.Entries<ServiceStatusHistory>().Should().BeEmpty();
    }

    [Fact]
    public void Payment_is_refused_before_the_service_is_completed()
    {
        using var db = NewContext();

        var result = ServicesEndpoints.ApplyPayment(AssignedJob(), PaymentStatus.Paid, TechId, db, Audit(db), null);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Invalid);
    }

    // ---------------------------------------------------------------- stock / dispatch

    [Fact]
    public async Task Keeping_a_job_in_stock_twice_is_a_no_op_the_second_time()
    {
        using var db = NewContext();
        var job = AssignedJob();
        job.ServiceStatus = ServiceStatus.Stocked;

        var result = await ServicesEndpoints.ApplyStockAsync(
            job, null, TechId, db, new StockLedgerService(db), new SerialService(db), Audit(db), null, default);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
        db.ChangeTracker.Entries<ServiceStatusHistory>().Should().BeEmpty();
    }

    /// <summary>Stocking used to be a pure status change on an ordinary job, and this test asserted
    /// that. It no longer is: pressing Keep in stock means the shop has decided to keep the machine,
    /// so the shelf is credited and the unit becomes the service centre's. What replaces the old
    /// guard is the pair of refusals below - a count that moves has to say what moved it.
    ///
    /// The status guard is still worth pinning here because it runs before any lookup, which is what
    /// keeps a wrong-status call from reaching the database at all.</summary>
    [Fact]
    public async Task Stocking_a_job_that_is_not_completed_is_refused_before_anything_is_read()
    {
        using var db = NewContext();
        var job = AssignedJob();   // still Assigned

        var result = await ServicesEndpoints.ApplyStockAsync(
            job, null, TechId, db, new StockLedgerService(db), new SerialService(db), Audit(db), null, default);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Invalid);
        job.ServiceStatus.Should().Be(ServiceStatus.Assigned);
        db.ChangeTracker.Entries<ServiceStatusHistory>().Should().BeEmpty();
    }

    /// <summary>Replaying Keep in stock on a job already stocked must stay a no-op. It matters more
    /// now than it did: the action credits the warehouse, so a replayed request that fell through
    /// would add a second unit to the shelf that does not exist.</summary>
    [Fact]
    public async Task Stocking_replayed_on_a_stocked_job_credits_nothing_a_second_time()
    {
        using var db = NewContext();
        var job = AssignedJob();
        job.ServiceStatus = ServiceStatus.Stocked;

        var result = await ServicesEndpoints.ApplyStockAsync(
            job, null, TechId, db, new StockLedgerService(db), new SerialService(db), Audit(db), null, default);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
        db.ChangeTracker.Entries<StockMovement>().Should().BeEmpty();
        db.ChangeTracker.Entries<ServiceStatusHistory>().Should().BeEmpty();
    }

    // ---------------------------------------------------------------- stocking refusals

    private static Part TrackedPart(bool serialTracked = true) => new()
    {
        Id = 5, ItemCode = "PS-100", Name = "Control board", IsSerialTracked = serialTracked,
    };

    /// <summary>The job has to name an item the shelf actually has a row for. Before this, stocking
    /// always succeeded and claimed nothing, so a job with a junk PS code was indistinguishable from
    /// one that had been counted.</summary>
    [Fact]
    public void Stocking_is_refused_when_the_PS_code_matches_no_catalogue_item()
    {
        var job = AssignedJob();
        job.PsCode = "PS-NOPE";

        ServicesEndpoints.StockBlockedReason(job, null, job.SerialNo)
            .Should().Contain("PS-NOPE").And.Contain("No catalogue item");
    }

    /// <summary>A job with no PS code at all gets a different instruction, because the fix is
    /// different: there is nothing to correct, something has to be set.</summary>
    [Fact]
    public void Stocking_is_refused_when_the_job_carries_no_PS_code()
    {
        var job = AssignedJob();
        job.PsCode = null;

        ServicesEndpoints.StockBlockedReason(job, null, job.SerialNo)
            .Should().Contain("no PS code");
    }

    /// <summary>A serial-tracked item must name its unit, for the same reason a serial-tracked
    /// component line must: otherwise the count moves and nothing records which physical thing
    /// moved it.</summary>
    [Fact]
    public void Stocking_a_serial_tracked_item_without_a_serial_is_refused()
    {
        var job = AssignedJob();
        job.PsCode = "PS-100";

        ServicesEndpoints.StockBlockedReason(job, TrackedPart(), "   ")
            .Should().Contain("serial-tracked");
    }

    /// <summary>An untracked item has no unit record either way - the quantity is the whole of what
    /// moved - so a missing serial is not a reason to refuse it.</summary>
    [Fact]
    public void Stocking_an_untracked_item_without_a_serial_is_allowed()
    {
        var job = AssignedJob();
        job.PsCode = "PS-100";

        ServicesEndpoints.StockBlockedReason(job, TrackedPart(serialTracked: false), null)
            .Should().BeNull();
    }

    [Fact]
    public void Stocking_a_resolved_serial_tracked_unit_is_allowed()
    {
        var job = AssignedJob();
        job.PsCode = "PS-100";

        ServicesEndpoints.StockBlockedReason(job, TrackedPart(), "SN-9001").Should().BeNull();
    }

    [Fact]
    public async Task Dispatch_replayed_on_a_dispatched_job_is_a_no_op()
    {
        using var db = NewContext();
        var job = AssignedJob();
        job.ServiceStatus = ServiceStatus.Dispatched;

        var result = await ServicesEndpoints.ApplyDispatchAsync(
            job, new DispatchRequest(), TechId, db, new SerialService(db), Audit(db), null, default);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
        db.ChangeTracker.Entries<ServiceStatusHistory>().Should().BeEmpty();
    }

    [Fact]
    public async Task Dispatch_is_refused_when_the_job_carries_no_traceable_number()
    {
        using var db = NewContext();
        var job = AssignedJob();
        job.ServiceStatus = ServiceStatus.Completed;

        var result = await ServicesEndpoints.ApplyDispatchAsync(
            job, new DispatchRequest(), TechId, db, new SerialService(db), Audit(db), null, default);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Invalid);
        result.Error.Should().Contain("outward reference");
    }

    // ---------------------------------------------------------------- outward reference

    [Fact]
    public void Setting_the_same_outward_reference_twice_adds_one_history_line()
    {
        using var db = NewContext();
        var job = AssignedJob();
        var req = new OutwardReferenceRequest("REF-1", null);

        ServicesEndpoints.ApplyOutwardReference(job, req, TechId, db, Audit(db), null);
        var replay = ServicesEndpoints.ApplyOutwardReference(job, req, TechId, db, Audit(db), null);

        replay.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
        job.OutwardReferenceNo.Should().Be("REF-1");
        db.ChangeTracker.Entries<ServiceStatusHistory>().Should().HaveCount(1);
    }

    [Fact]
    public void Setting_an_outward_reference_never_clears_an_existing_dc_number()
    {
        using var db = NewContext();
        var job = AssignedJob();
        job.OutwardDcNo = "DC-9";

        ServicesEndpoints.ApplyOutwardReference(job, new OutwardReferenceRequest("REF-1", null), TechId, db, Audit(db), null);

        job.OutwardDcNo.Should().Be("DC-9");
    }
}
