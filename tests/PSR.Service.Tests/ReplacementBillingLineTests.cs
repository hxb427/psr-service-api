using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Services;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>A replacement hands the customer a unit off the shelf. Two routes do that — the total-loss
/// replacement and the advance swap — and both finish the job in the pending-dispatch queue, where it
/// is billed like any other.
///
/// The total-loss route used to add no line for the unit it gave away. A total-loss job carries no
/// lines of its own either (components and charges are refused once it is marked), so an
/// OUT-OF-WARRANTY replacement priced to nothing: the generate form opened with a rate of 0 and the PI
/// billed the customer nothing for a unit taken off the rack. Both routes now build the line through
/// the same helper, which is what these pin.</summary>
public class ReplacementBillingLineTests
{
    private static ServiceJob Job(WarrantyStatus warranty) => new()
    {
        Id = 7,
        ServiceNo = "SVC0007",
        SerialNo = "SN-IN",
        WarrantyStatus = warranty,
        ServiceStatus = ServiceStatus.ReplacementApprovalPending,
        IsTotalLoss = true,
    };

    private static Part Outgoing(decimal rate = 4000m) => new()
    {
        Id = 31, ItemCode = "PS-100", Name = "Controller board", CustomerRate = rate,
    };

    /// <summary>The case that was broken: the customer pays for the unit, so the job has to carry a
    /// line that says what it cost.</summary>
    [Fact]
    public void An_out_of_warranty_replacement_is_billed()
    {
        var line = ServicesEndpoints.ReplacementBillingLine(Job(WarrantyStatus.OutOfWarranty), Outgoing(), "SN-OUT");

        line.Should().NotBeNull();
        line!.LineType.Should().Be(ServiceLineType.Replacement);
        line.ServiceId.Should().Be(7);
        line.PartId.Should().Be(31);
        line.Qty.Should().Be(1);
        line.UnitPrice.Should().Be(4000m);
        line.Amount.Should().Be(4000m);
        // The unit that LEFT, not the one that came in — this is the serial the customer walks out with.
        line.ReplacementSerialNo.Should().Be("SN-OUT");
        line.Description.Should().Contain("Controller board");
    }

    /// <summary>Under cover there is nothing to charge, and no line either: a line at zero would read on
    /// the document as a unit priced at nothing rather than one given under warranty.</summary>
    [Fact]
    public void An_in_warranty_replacement_is_not_billed()
        => ServicesEndpoints.ReplacementBillingLine(Job(WarrantyStatus.InWarranty), Outgoing(), "SN-OUT")
            .Should().BeNull();

    /// <summary>A job old enough to predate the in/out split is not under cover, so it bills — leaving it
    /// unbilled is the same silent zero the fix is about.</summary>
    [Fact]
    public void A_job_of_unknown_warranty_is_billed()
        => ServicesEndpoints.ReplacementBillingLine(Job(WarrantyStatus.Unknown), Outgoing(), "SN-OUT")
            .Should().NotBeNull();

    /// <summary>A catalogue item with no customer rate still gets its line. The zero is then a priced-at-
    /// nothing catalogue entry, which the desk can see on the job and hand-price on the form — not a
    /// missing line, which it could not see at all.</summary>
    [Fact]
    public void An_unpriced_part_still_produces_a_line()
    {
        var line = ServicesEndpoints.ReplacementBillingLine(
            Job(WarrantyStatus.OutOfWarranty), Outgoing(rate: 0m), "SN-OUT");

        line.Should().NotBeNull();
        line!.Amount.Should().Be(0m);
    }

    /// <summary>The serial is stored trimmed, and a blank one stored as nothing rather than as "" —
    /// cancelling a swap matches its billing line on this field, and an empty string is not a serial.</summary>
    [Theory]
    [InlineData("  SN-OUT  ", "SN-OUT")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void The_outgoing_serial_is_normalised(string? given, string? expected)
        => ServicesEndpoints.ReplacementBillingLine(Job(WarrantyStatus.OutOfWarranty), Outgoing(), given)!
            .ReplacementSerialNo.Should().Be(expected);
}
