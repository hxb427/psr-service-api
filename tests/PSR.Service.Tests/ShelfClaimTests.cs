using FluentAssertions;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// The shelf's guard against two people taking the same unit.
///
/// The warehouse cannot be over-issued because its quantity is taken by a conditional UPDATE that
/// fails when the balance would go negative (<c>StockLedgerService.GuardedDecrementAsync</c>). The
/// shelf has no quantity to guard — it IS the <c>component_serials</c> rows that are SHELFED in the
/// service centre's custody — so <see cref="SerialService.ClaimFromShelfAsync"/> points the same idiom
/// at the row instead: the WHERE carries the condition, and nought rows affected is the refusal.
///
/// WHAT IS AND IS NOT TESTED HERE, because it is the whole shape of this file. The race cannot be
/// reproduced without a database, and this suite opens none; what makes the claim a lock rather than
/// a re-check is InnoDB's behaviour (an UPDATE holds an exclusive row lock to commit, and judges its
/// WHERE against the latest committed row rather than the transaction's snapshot), which is a property
/// of the server and not of this code. So what is pinned here is the DECISION: given what the form was
/// offered and what the claim came back with, what must happen. That is the part somebody could get
/// wrong while reading, and the wrong answer is specific — see below.
/// </summary>
public class ShelfClaimTests
{
    private const string Sn = "SN-4417";
    private const long PartId = 5;

    // ---------------------------------------------------------------- the one failing combination

    /// <summary>The race, lost. The form offered a unit off the shelf and the claim could not get it,
    /// so somebody else took it between the pick list being drawn and this transaction opening.
    ///
    /// It must refuse, and the refusal must say nothing was handed over — the person reading it is
    /// standing at a counter deciding whether to go and fetch a machine.</summary>
    [Fact]
    public void A_shelf_unit_that_has_gone_since_the_form_opened_is_refused()
    {
        var reason = SerialService.ShelfClaimLostReason(promisedShelf: true, claimed: false, Sn);

        reason.Should().NotBeNull();
        reason.Should().Contain(Sn, "the person has to know WHICH unit to stop looking for");
        reason.Should().Contain("no longer on the shelf");
        reason.Should().Contain("Nothing has been handed over");
    }

    /// <summary>THE WRONG ANSWER THIS EXISTS TO PREVENT: falling back to the warehouse.
    ///
    /// It is tempting because it reads as resilience — the unit is not on the shelf, so take it out of
    /// stock instead — and it is silently, expensively wrong. The warehouse never held that unit, so
    /// the debit is against a quantity that does not correspond to anything on a rack: the shop ends
    /// up one unit short on paper, and the customer has been handed nothing at all. A refusal is the
    /// only honest outcome, which is why this returns a reason rather than a pool.</summary>
    [Fact]
    public void A_lost_claim_never_silently_becomes_a_warehouse_issue()
    {
        // The signature is the guarantee: there is no third state for the caller to fall into, and
        // the only non-null result is the one that stops the handover.
        SerialService.ShelfClaimLostReason(promisedShelf: true, claimed: false, Sn)
            .Should().NotBeNull("a lost shelf claim has to stop, not re-route");
    }

    // ---------------------------------------------------------------- the three ordinary ones

    /// <summary>The ordinary shelf handover: offered and taken.</summary>
    [Fact]
    public void A_shelf_unit_that_is_still_there_goes_through()
    {
        SerialService.ShelfClaimLostReason(promisedShelf: true, claimed: true, Sn)
            .Should().BeNull();
    }

    /// <summary>A serial the shop has never seen. This is warehouse stock being named as it goes out,
    /// which is how a serial-tracked part's bought-in stock enters tracking at all — it is received as
    /// a quantity and named at issue. It must keep working exactly as it did, so a failed claim on its
    /// own is not an error.</summary>
    [Fact]
    public void An_unknown_serial_is_warehouse_stock_and_not_a_lost_race()
    {
        SerialService.ShelfClaimLostReason(promisedShelf: false, claimed: false, Sn)
            .Should().BeNull("the warehouse path has no shelf unit to lose");
    }

    /// <summary>Somebody shelved that very serial between the form opening and the claim. The claim is
    /// the more recent fact and the unit is genuinely in hand, so it is taken off the shelf rather
    /// than bought a second time out of a warehouse quantity.</summary>
    [Fact]
    public void A_unit_shelved_after_the_form_opened_is_taken_off_the_shelf()
    {
        SerialService.ShelfClaimLostReason(promisedShelf: false, claimed: true, Sn)
            .Should().BeNull();
    }

    // ---------------------------------------------------------------- the table, in full

    /// <summary>All four combinations in one place, so the asymmetry is visible: only the top-left
    /// corner fails. Stated separately from the cases above because the cases explain WHY and this
    /// pins the shape — somebody widening the rule has to come through here.</summary>
    [Theory]
    [InlineData(true, true, false)]    // offered a shelf unit, got it
    [InlineData(true, false, true)]    // offered a shelf unit, somebody else took it  → REFUSE
    [InlineData(false, false, false)]  // unknown serial, warehouse stock
    [InlineData(false, true, false)]   // shelved in between, take it off the shelf
    public void Only_a_promised_shelf_unit_that_could_not_be_claimed_refuses(
        bool promisedShelf, bool claimed, bool shouldRefuse)
    {
        var reason = SerialService.ShelfClaimLostReason(promisedShelf, claimed, Sn);

        (reason is not null).Should().Be(shouldRefuse);
    }

    // ---------------------------------------------------------------- the claim writes nothing but the lock

    /// <summary>The claim deliberately sets no status, and that is worth protecting because the
    /// obvious implementation does.
    ///
    /// Moving the row to its destination here would leave the real transition
    /// (<c>InstallToCustomerAsync</c> / <c>CaptureOnIssueAsync</c>) writing a history row whose old
    /// status was already the new one — so the trail would stop saying the unit came off the shelf,
    /// which is the one thing the shelf's history is kept for. Touching <c>last_updated_at</c> takes
    /// the same exclusive row lock and answers the same question.
    ///
    /// Asserted against the real statement the method executes, not a copy of it: a copied string
    /// would pass while the code it describes was wrong, which is worse than leaving it uncovered.</summary>
    [Fact]
    public void The_claim_guards_on_status_without_writing_one()
    {
        var sql = SerialService.ClaimFromShelfSql(PartId, Sn).Format;

        sql.Should().Contain("`status` = 'Shelfed'", "the condition is what makes it a guard");
        sql.Should().Contain("`owner_type` = 'ServiceCenter'",
            "a unit in a shelf status held by somebody else was never shelf stock");
        sql.Should().Contain("SET `last_updated_at`");
        sql.Should().NotContain("SET `status`",
            "writing the destination here would destroy the Shelfed -> ... history row");
    }

    /// <summary>The unit is addressed by part AND serial, which is the pair the unique index is on —
    /// a serial alone is not unique across parts, so claiming by it could lock somebody else's unit.
    /// Both arrive as parameters rather than concatenated text, so a serial is never SQL.</summary>
    [Fact]
    public void The_claim_addresses_one_unit_by_part_and_serial_as_parameters()
    {
        var statement = SerialService.ClaimFromShelfSql(PartId, Sn);

        statement.GetArguments().Should().Equal(PartId, Sn);
        statement.Format.Should().NotContain(Sn, "the serial goes in as a parameter, never as text");
    }
}
