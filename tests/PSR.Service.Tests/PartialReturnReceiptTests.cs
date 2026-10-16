using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// A return shipment is an envelope, not the thing being received. Ten units in one box can turn up
/// as eight good, one smashed and one absent, and the all-or-nothing acknowledgement this replaces
/// could only record "all ten arrived perfectly" — so the warehouse gained two units nobody had, and
/// the serial ledger swore a broken unit and a lost one were re-issuable stock.
///
/// These fix the two rules that make the per-unit version safe: a call may only resolve units that
/// are genuinely still outstanding on that shipment, and a quantity-only receipt may never claim more
/// than was sent.
/// </summary>
public class PartialReturnReceiptTests
{
    private static AckStockReturnSerialLine Line(long id, string status) => new(id, status);

    /// <summary>What an empty body has always meant, and must keep meaning: deployed phones and
    /// desktops post this route with no body at all.</summary>
    [Fact]
    public void Naming_no_units_books_in_everything_still_outstanding()
    {
        var (verdicts, error) = StockReturnsEndpoints.PlanUnitReceipt([7, 8, 9], null);

        error.Should().BeNull();
        verdicts.Should().Equal(
            (7, SerialAckStatus.Received), (8, SerialAckStatus.Received), (9, SerialAckStatus.Received));
    }

    [Fact]
    public void Units_not_named_are_left_outstanding()
    {
        var (verdicts, error) = StockReturnsEndpoints.PlanUnitReceipt([7, 8, 9], [Line(8, "Received")]);

        error.Should().BeNull();
        verdicts.Should().Equal((8, SerialAckStatus.Received));
    }

    [Fact]
    public void Each_unit_carries_its_own_verdict()
    {
        var (verdicts, error) = StockReturnsEndpoints.PlanUnitReceipt(
            [7, 8, 9], [Line(7, "Received"), Line(8, "Defective"), Line(9, "Missing")]);

        error.Should().BeNull();
        verdicts.Should().Equal(
            (7, SerialAckStatus.Received), (8, SerialAckStatus.Defective), (9, SerialAckStatus.Missing));
    }

    /// <summary>A unit someone else booked in an hour ago, or a serial id from a different shipment.
    /// Both reach here as "not outstanding", and both have to be refused rather than quietly skipped:
    /// skipping reports success for work that never happened.</summary>
    [Fact]
    public void A_unit_that_is_not_outstanding_is_refused_by_id()
    {
        var (_, error) = StockReturnsEndpoints.PlanUnitReceipt([7, 8], [Line(99, "Received")]);

        error.Should().Contain("99").And.Contain("awaiting receipt");
    }

    /// <summary>Resolving the same unit twice in one call would count it twice against the shelf.</summary>
    [Fact]
    public void The_same_unit_cannot_be_resolved_twice_in_one_call()
    {
        var (_, error) = StockReturnsEndpoints.PlanUnitReceipt(
            [7, 8], [Line(7, "Received"), Line(7, "Missing")]);

        error.Should().Contain("named twice");
    }

    [Fact]
    public void An_unknown_verdict_is_named_rather_than_silently_ignored()
    {
        var (_, error) = StockReturnsEndpoints.PlanUnitReceipt([7], [Line(7, "Lost")]);

        error.Should().Contain("Lost");
    }

    [Theory]
    [InlineData("received")]
    [InlineData("DEFECTIVE")]
    [InlineData("Missing")]
    public void Verdicts_are_read_case_insensitively(string status)
    {
        var (verdicts, error) = StockReturnsEndpoints.PlanUnitReceipt([7], [Line(7, status)]);

        error.Should().BeNull();
        verdicts.Should().HaveCount(1);
    }

    // ---- quantity-only shipments ----

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(10)]
    public void A_receiver_may_book_in_anything_up_to_what_was_sent(int received)
    {
        StockReturnsEndpoints.QuantityReceiptError(received, 10, "RET-1").Should().BeNull();
    }

    /// <summary>The rule that matters: a receipt describes one shipment. Booking in eleven of ten
    /// invents a unit that was never dispatched, and the warehouse count carries the invention
    /// forward for ever.</summary>
    [Fact]
    public void Booking_in_more_than_was_sent_is_refused()
    {
        StockReturnsEndpoints.QuantityReceiptError(11, 10, "RET-1")
            .Should().Contain("between 0 and the 10 sent");
    }

    [Fact]
    public void A_negative_receipt_is_refused()
    {
        StockReturnsEndpoints.QuantityReceiptError(-1, 10, "RET-1").Should().NotBeNull();
    }
}
