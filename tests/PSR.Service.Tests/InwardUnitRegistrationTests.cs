using FluentAssertions;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Services;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// A part booked in over the counter joins the serial ledger, so the same unit coming back next year
/// is a record rather than somebody's memory. A whole machine does not: the factory records already
/// register those and the shop only reads them.
///
/// The rule that matters most here is the one about replacements. A job that handed the customer a
/// different unit must not also hand back the one on the job — the customer left with one item, not
/// two, and the ledger has to agree with the counter.
/// </summary>
public class InwardUnitRegistrationTests
{
    private const long UserId = 42;

    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);
    private static IAuditService Audit(AppDbContext db) => new AuditService(db);

    private static ServiceJob CompletedJob() => new()
    {
        Id = 1,
        ServiceNo = "SVC0001",
        SerialNo = "SN-777",
        PsCode = "PS-100",
        WarrantyStatus = WarrantyStatus.InWarranty,   // exempt from the outward-reference rule
        ServiceStatus = ServiceStatus.Completed,
        SourceComponentSerialId = 9,                  // the unit registered at inward
    };

    /// <summary>The customer walked out with the replacement. Booking the original back to them would
    /// put a unit at a customer who never received it, while it sits on the shop's rack.</summary>
    [Fact]
    public void A_job_that_issued_a_replacement_does_not_hand_back_its_own_unit()
    {
        var job = CompletedJob();
        job.ReplacementSerialNo = "SN-REPL-1";

        ServicesEndpoints.DispatchReturnsUnitToParty(job).Should().BeFalse();
    }

    /// <summary>The ordinary case does hand the unit back - that is what dispatch means.</summary>
    [Fact]
    public void An_ordinary_job_hands_its_unit_back_on_dispatch()
    {
        ServicesEndpoints.DispatchReturnsUnitToParty(CompletedJob()).Should().BeTrue();
    }

    /// <summary>A job carrying no registered unit has nothing to hand back either way - a whole
    /// machine, or a part the catalogue does not follow unit by unit.</summary>
    [Fact]
    public void A_job_with_no_registered_unit_moves_nothing()
    {
        var job = CompletedJob();
        job.SourceComponentSerialId = null;

        ServicesEndpoints.DispatchReturnsUnitToParty(job).Should().BeFalse();
    }

    /// <summary>Dispatch itself still works for a job with no unit on it, which is every job booked
    /// before inward registration existed.</summary>
    [Fact]
    public async Task Dispatching_a_job_without_a_unit_still_succeeds()
    {
        using var db = NewContext();
        var job = CompletedJob();
        job.SourceComponentSerialId = null;

        var result = await ServicesEndpoints.ApplyDispatchAsync(
            job, new DispatchRequest(), UserId, db, new SerialService(db), Audit(db), null, default);

        result.Status.Should().Be(ServicesEndpoints.ApplyStatus.Applied);
        job.ServiceStatus.Should().Be(ServiceStatus.Dispatched);
    }

    // ---------------------------------------------------------------- what the ledger will hold

    /// <summary>UNDER_REPAIR is the status a customer's unit sits at while the shop has it, and it is
    /// deliberately not re-issuable — a customer's machine must never be offered on the next issue.
    /// This pins that, because registering inward items puts many more units into that status.</summary>
    [Fact]
    public void A_unit_in_for_service_is_never_offered_for_issue()
    {
        SerialService.ReIssuable.Should().NotContain(SerialStatus.UnderRepair);
        SerialService.ReIssuable.Should().BeEquivalentTo(
            [SerialStatus.ReturnedToSc, SerialStatus.Repaired]);
    }

    /// <summary>And it is not shippable on either kind of technician return either: it is at the
    /// service centre already.</summary>
    [Fact]
    public void A_unit_in_for_service_cannot_ride_a_technician_return()
    {
        SerialService.ShippableFor(StockReturnKind.GoodStock).Should().NotContain(SerialStatus.UnderRepair);
        SerialService.ShippableFor(StockReturnKind.Faulty).Should().NotContain(SerialStatus.UnderRepair);
    }
}
