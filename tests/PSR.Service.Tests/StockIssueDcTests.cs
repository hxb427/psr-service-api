using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Whether an issue of stock carries a delivery challan, and where its number comes from.
///
/// A challan describes goods leaving the premises, so it follows the same rule the courier and tracking
/// boxes already do: field technicians only. An in-house technician collects at the counter and the
/// goods never go anywhere, so a number is refused rather than quietly dropped — a store told it cannot
/// use a challan here can act on that; a number silently discarded is found out later, by its absence.</summary>
public class StockIssueDcTests
{
    private static User Tech(bool field) => new()
    {
        Id = field ? 11 : 12, Username = field ? "fieldtech" : "benchtech", IsFieldTechnician = field,
    };

    [Fact]
    public void An_issue_that_asks_for_nothing_carries_no_challan()
        => StockRequestsEndpoints.PlanDc(null, autoGenerate: false, Tech(field: true))
            .Should().Be((null, false, null));

    [Fact]
    public void A_blank_dc_is_not_a_challan()
        => StockRequestsEndpoints.PlanDc("   ", autoGenerate: false, Tech(field: true))
            .Should().Be((null, false, null));

    /// <summary>Auto-generate with nothing typed: the server allocates from the SDC book.</summary>
    [Fact]
    public void Auto_generate_asks_for_a_number()
        => StockRequestsEndpoints.PlanDc(null, autoGenerate: true, Tech(field: true))
            .Should().Be((null, true, null));

    [Fact]
    public void A_typed_number_is_used_as_it_stands()
        => StockRequestsEndpoints.PlanDc(" SDC-2026-0041 ", autoGenerate: false, Tech(field: true))
            .Should().Be(("SDC-2026-0041", false, null));

    /// <summary>The store is copying a docket it already holds. Allocating a second number for the same
    /// consignment would leave a gap in the book for nothing, so what was typed wins.</summary>
    [Fact]
    public void A_typed_number_wins_over_the_auto_switch()
        => StockRequestsEndpoints.PlanDc("SDC-2026-0041", autoGenerate: true, Tech(field: true))
            .Should().Be(("SDC-2026-0041", false, null));

    [Theory]
    [InlineData("SDC-2026-0041", false)]
    [InlineData(null, true)]
    public void An_in_house_technician_cannot_be_given_a_challan(string? typed, bool auto)
    {
        var (dcNo, allocate, error) = StockRequestsEndpoints.PlanDc(typed, auto, Tech(field: false));

        dcNo.Should().BeNull();
        allocate.Should().BeFalse();
        error.Should().NotBeNull().And.Contain("benchtech");
    }

    /// <summary>An in-house issue that never asked for a challan is not an error — it is every counter
    /// handover the store has ever done.</summary>
    [Fact]
    public void An_in_house_issue_without_a_challan_is_fine()
        => StockRequestsEndpoints.PlanDc(null, autoGenerate: false, Tech(field: false))
            .Should().Be((null, false, null));

    /// <summary>A request whose requester cannot be loaded is treated as in transit everywhere else in
    /// the issue path, so it is allowed a challan here too rather than being refused on a null.</summary>
    [Fact]
    public void An_unknown_technician_is_allowed_a_challan()
        => StockRequestsEndpoints.PlanDc(null, autoGenerate: true, technician: null)
            .Should().Be((null, true, null));
}
