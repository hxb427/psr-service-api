using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// Issuing to an in-house technician records the counter handover as its own acknowledgement, in the
/// same unit of work as the issue that created the movement. The movement is therefore unsaved at that
/// point and its id is still 0, so the acknowledgement has to point at the movement itself and let EF
/// fill the column in once the insert has run.
///
/// Writing the id instead put a 0 in the column, which the foreign key refused: every direct issue of a
/// part carrying no serials to an in-house technician answered 500, while field technicians (no
/// acknowledgement row) and serial-tracked parts (already saved, for the serial links) went through.
/// These cover the linkage itself rather than the endpoint, because it is the linkage that was wrong.
/// </summary>
public class StockHandoverAckTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    /// <summary>A movement that has been through the database hands its id to the acknowledgement by
    /// the reference alone — nothing assigns the foreign key.</summary>
    [Fact]
    public void A_handover_acknowledgement_takes_its_movement_id_from_the_movement()
    {
        using var db = NewContext();
        var movement = new StockMovement
        {
            Id = 7, PartId = 3, MovementType = MovementType.Issue, Quantity = 2,
            TechnicianId = 11, PerformedByUserId = 4,
        };
        db.StockMovements.Attach(movement);

        StockRequestsEndpoints.AddHandoverAck(db, movement, qty: 2, uid: 4);

        var ack = db.ChangeTracker.Entries<StockIssueAck>().Single().Entity;
        ack.StockMovementId.Should().Be(7);
        ack.QtyReceived.Should().Be(2);
    }

    /// <summary>The case that broke: the movement has not been saved, so there is no id to copy. The
    /// acknowledgement must still leave holding the movement, or it is bound for a 0 the database
    /// rejects.</summary>
    [Fact]
    public void An_unsaved_movement_is_linked_by_reference_rather_than_by_a_zero_id()
    {
        using var db = NewContext();
        var movement = new StockMovement
        {
            PartId = 3, MovementType = MovementType.Issue, Quantity = 1,
            TechnicianId = 11, PerformedByUserId = 4, CreditedOnIssue = true,
        };
        db.StockMovements.Add(movement);

        StockRequestsEndpoints.AddHandoverAck(db, movement, qty: 1, uid: 4);

        var ack = db.ChangeTracker.Entries<StockIssueAck>().Single().Entity;
        ack.StockMovement.Should().BeSameAs(movement);
    }

    /// <summary>The mapping that makes the reference count for anything. Configured without a
    /// navigation, EF has nothing to fix the column up from and writes whatever the property happens to
    /// hold, which is how the 0 reached the database in the first place.</summary>
    [Fact]
    public void The_acknowledgement_reaches_its_movement_through_a_navigation()
    {
        using var db = NewContext();

        var fk = db.Model.FindEntityType(typeof(StockIssueAck))!
            .GetForeignKeys()
            .Single(f => f.PrincipalEntityType.ClrType == typeof(StockMovement));

        fk.DependentToPrincipal.Should().NotBeNull(
            "EF only fills in stock_movement_id when the acknowledgement can reach the movement");
        fk.DependentToPrincipal!.Name.Should().Be(nameof(StockIssueAck.StockMovement));
        fk.Properties.Single().Name.Should().Be(nameof(StockIssueAck.StockMovementId));
    }
}
