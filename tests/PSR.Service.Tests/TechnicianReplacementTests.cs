using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Services;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// A technician may now issue the replacement for a unit they have just written off, from the
/// In-service tab, instead of completing the job and waiting for the dispatch desk to do it.
///
/// That is a widening of who may take a unit off the shelf, so what these pin down is mostly what it
/// did NOT widen: the mark is still required, the job must still be the technician's own, and every
/// other status still refuses. The writes need a database, like every other ledger path in this suite,
/// so the rule itself is a pure function and this is it.
/// </summary>
public class TechnicianReplacementTests
{
    private static ServiceJob Job(ServiceStatus status, bool totalLoss = false) => new()
    {
        Id = 1, ServiceNo = "SVC0001", SerialNo = "SN1", PsCode = "PS-100",
        ServiceStatus = status, IsTotalLoss = totalLoss,
    };

    // ---------------------------------------------------------------- the bench route

    /// <summary>The whole point: the person who condemned the unit hands over its replacement.</summary>
    [Fact]
    public void The_assigned_technician_can_replace_from_an_in_service_total_loss()
    {
        var route = ServicesEndpoints.ResolveReplaceRoute(
            Job(ServiceStatus.InService, totalLoss: true), canManage: false, isAssignedTechnician: true);

        route.Route.Should().Be(ServicesEndpoints.ReplaceRoute.Bench);
        route.Forbidden.Should().BeFalse();
        route.Error.Should().BeNull();
    }

    /// <summary>The mark is what says there is something to replace. Without it a technician could
    /// hand a unit off the shelf to any customer whose job they happened to be holding — which is a
    /// different feature entirely, and the one the swap route exists for.</summary>
    [Fact]
    public void An_in_service_job_that_is_not_a_total_loss_has_nothing_to_replace()
    {
        var route = ServicesEndpoints.ResolveReplaceRoute(
            Job(ServiceStatus.InService), canManage: false, isAssignedTechnician: true);

        route.Route.Should().BeNull();
        route.Forbidden.Should().BeFalse("it is a workflow refusal, not a permissions one");
        route.Error.Should().Contain("not marked total loss");
    }

    /// <summary>Same rule as every other bench action. Someone else's job is not a technician's to
    /// finish, and finishing it is exactly what the bench route does.</summary>
    [Fact]
    public void A_technician_cannot_replace_on_a_job_that_is_not_theirs()
    {
        var route = ServicesEndpoints.ResolveReplaceRoute(
            Job(ServiceStatus.InService, totalLoss: true), canManage: false, isAssignedTechnician: false);

        route.Forbidden.Should().BeTrue();
        route.Route.Should().BeNull();
    }

    /// <summary>A supervisory role may use either door. They could already replace from the approval
    /// route, and shutting them out of the bench one would leave the technician the only person able
    /// to finish a job they had walked away from.</summary>
    [Fact]
    public void A_manager_can_also_replace_from_the_bench_route()
    {
        var route = ServicesEndpoints.ResolveReplaceRoute(
            Job(ServiceStatus.InService, totalLoss: true), canManage: true, isAssignedTechnician: false);

        route.Route.Should().Be(ServicesEndpoints.ReplaceRoute.Bench);
    }

    // ---------------------------------------------------------------- the approval route is unchanged

    /// <summary>The original route still admits exactly who the DispatchManage policy admitted, which
    /// matters because the route no longer carries that policy — the handler checks it instead, since
    /// "the technician this row is assigned to" is not something a role policy can say.</summary>
    [Fact]
    public void A_dispatch_role_still_replaces_from_replacement_approval_pending()
    {
        var route = ServicesEndpoints.ResolveReplaceRoute(
            Job(ServiceStatus.ReplacementApprovalPending), canManage: true, isAssignedTechnician: false);

        route.Route.Should().Be(ServicesEndpoints.ReplaceRoute.Approval);
        route.Forbidden.Should().BeFalse();
    }

    /// <summary>Moving the check off the route must not have opened the approval door to the bench.
    /// A job parked on approval has been handed over; deciding what goes back is the desk's call, and
    /// the technician's own route is the in-service one they still hold.</summary>
    [Fact]
    public void A_technician_cannot_replace_from_replacement_approval_pending()
    {
        var job = Job(ServiceStatus.ReplacementApprovalPending);

        var route = ServicesEndpoints.ResolveReplaceRoute(job, canManage: false, isAssignedTechnician: true);

        route.Forbidden.Should().BeTrue();
        route.Route.Should().BeNull();
    }

    // ---------------------------------------------------------------- every other status still refuses

    [Theory]
    [InlineData(ServiceStatus.Inward)]
    [InlineData(ServiceStatus.Assigned)]
    [InlineData(ServiceStatus.Acknowledged)]
    [InlineData(ServiceStatus.Completed)]
    [InlineData(ServiceStatus.Dispatched)]
    [InlineData(ServiceStatus.Stocked)]
    [InlineData(ServiceStatus.TotalLoss)]
    [InlineData(ServiceStatus.Replaced)]
    public void No_other_status_can_issue_a_replacement(ServiceStatus status)
    {
        // Marked total loss AND held by the assigned technician AND a manager: nothing about the
        // caller rescues a status that is not one of the two.
        var route = ServicesEndpoints.ResolveReplaceRoute(
            Job(status, totalLoss: true), canManage: true, isAssignedTechnician: true);

        route.Route.Should().BeNull();
        route.Error.Should().Contain("can only be issued");
    }

    // ---------------------------------------------------------------- the unit has to leave the shelf

    /// <summary>A serial-tracked unit was always recorded against the customer. Unchanged.</summary>
    [Fact]
    public void A_serial_tracked_replacement_is_recorded_against_the_customer()
    {
        ServicesEndpoints.ReplacementUnitLeavesSerialTrail(isSerialTracked: true, fromShelf: false)
            .Should().BeTrue();
    }

    /// <summary>The bug this closes. A stocked job shelves the unit it names whether or not its part is
    /// flagged serial-tracked, so the shelf really does hold units of untracked parts — and a shelf
    /// issue moves no quantity by design, because the SHELFED row IS the shelf. So on an untracked
    /// shelf unit the old test debited nothing AND left the row on SHELFED: the same unit could be
    /// handed out for ever, a replacement in the ledger each time, the shelf never going down.</summary>
    [Fact]
    public void An_untracked_unit_taken_off_the_shelf_still_leaves_the_shelf()
    {
        ServicesEndpoints.ReplacementUnitLeavesSerialTrail(isSerialTracked: false, fromShelf: true)
            .Should().BeTrue("the status change is the only thing that takes a unit off the shelf");
    }

    /// <summary>The one case with nothing to record: an untracked part coming out of the warehouse
    /// quantity. There is no unit to name, which is what being untracked means, and the quantity
    /// decrement is the whole of the movement.</summary>
    [Fact]
    public void An_untracked_unit_out_of_warehouse_stock_records_no_unit()
    {
        ServicesEndpoints.ReplacementUnitLeavesSerialTrail(isSerialTracked: false, fromShelf: false)
            .Should().BeFalse();
    }
}
