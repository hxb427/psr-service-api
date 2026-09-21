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

    /// <summary>An ordinary job holds a customer's machine, not a catalogue part, so stocking it must
    /// stay a pure status change. Only a job raised off a faulty field return carries a source serial
    /// and moves stock - this is the guard on that split.</summary>
    [Fact]
    public async Task Stocking_an_ordinary_job_records_no_stock_movement()
    {
        using var db = NewContext();
        var job = AssignedJob();
        job.ServiceStatus = ServiceStatus.Completed;

        var result = await ServicesEndpoints.ApplyStockAsync(
            job, null, TechId, db, new StockLedgerService(db), new SerialService(db), Audit(db), null, default);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
        job.ServiceStatus.Should().Be(ServiceStatus.Stocked);
        db.ChangeTracker.Entries<StockMovement>().Should().BeEmpty();
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
