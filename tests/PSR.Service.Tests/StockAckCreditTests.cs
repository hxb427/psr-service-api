using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// Stock becomes a technician's when they acknowledge it, not when it is dispatched. A courier issue
/// debits the warehouse and credits nobody until it arrives; the acknowledgement credits what
/// actually turned up and is usable. These cover the two rules that make that safe: the quantities
/// have to agree with the per-unit verdicts, and an issue that was already credited must never be
/// credited a second time.
/// </summary>
public class StockAckCreditTests
{
    private static string[] Verdicts(int received, int defective, int missing) =>
        [.. Enumerable.Repeat("Received", received),
           .. Enumerable.Repeat("Defective", defective),
           .. Enumerable.Repeat("Missing", missing)];

    [Fact]
    public void Quantities_that_agree_with_the_units_are_accepted()
    {
        StockAcksEndpoints.SerialQuantityMismatch(Verdicts(2, 1, 1), 2, 1, 1)
            .Should().BeNull();
    }

    /// <summary>The case the guard exists for: booking stock as received while marking the units
    /// missing would credit a balance the serial ledger says never arrived.</summary>
    [Fact]
    public void Claiming_receipt_of_units_marked_missing_is_refused()
    {
        StockAcksEndpoints.SerialQuantityMismatch(Verdicts(0, 0, 3), 3, 0, 0)
            .Should().Contain("do not match");
    }

    [Fact]
    public void Defective_units_counted_as_received_are_refused()
    {
        StockAcksEndpoints.SerialQuantityMismatch(Verdicts(1, 2, 0), 3, 0, 0)
            .Should().Contain("do not match");
    }

    [Fact]
    public void An_unknown_verdict_is_named_rather_than_silently_ignored()
    {
        StockAcksEndpoints.SerialQuantityMismatch(["Received", "Lost"], 2, 0, 0)
            .Should().Contain("Lost");
    }

    [Fact]
    public void A_fully_received_shipment_agrees_with_itself()
    {
        StockAcksEndpoints.SerialQuantityMismatch(Verdicts(5, 0, 0), 5, 0, 0)
            .Should().BeNull();
    }

    /// <summary>Movements written before receipts were split out credited the technician at dispatch.
    /// The column backfills to true so acknowledging one of them later cannot double the holding;
    /// only rows the new issue path writes say otherwise.</summary>
    [Fact]
    public void Movements_default_to_having_been_credited_at_issue()
    {
        new StockMovement().CreditedOnIssue.Should().BeTrue();
    }
}
