using FluentAssertions;
using PSR.Service.Api.SpareSales;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Marking a sale sold is the one action that empties the shelf, and it can happen days after
/// the sale was written — by which time the stock behind it has moved. The guarded decrement makes
/// overselling impossible either way; what is tested here is being TOLD, once, what the warehouse
/// cannot cover, instead of discovering it one refused item at a time.</summary>
public class SpareSaleMarkSoldShortfallTests
{
    private static readonly (long PartId, string ItemCode, int Qty)[] OneLine =
        [(1L, "950000220", 5)];

    [Fact]
    public void Nothing_is_refused_when_the_warehouse_covers_the_sale()
        => SpareSalesEndpoints.ShortfallRefusal("SAL00099", OneLine, new Dictionary<long, int> { [1L] = 5 })
            .Should().BeNull();

    /// <summary>Exactly enough is enough. An off-by-one here would refuse the last of something, which is
    /// the most ordinary sale a counter makes.</summary>
    [Theory]
    [InlineData(5, null)]      // exactly enough
    [InlineData(6, null)]      // more than enough
    [InlineData(4, "4 in warehouse stock, 5 on this sale")]
    [InlineData(0, "0 in warehouse stock, 5 on this sale")]
    public void The_boundary_is_at_the_quantity_asked_for(int onHand, string? expectedFragment)
    {
        var refusal = SpareSalesEndpoints.ShortfallRefusal(
            "SAL00099", OneLine, new Dictionary<long, int> { [1L] = onHand });

        if (expectedFragment is null) refusal.Should().BeNull();
        else refusal.Should().Contain(expectedFragment);
    }

    /// <summary>A part with no balance row has never been booked in, which is nothing in stock — not a
    /// part that may be passed through unchecked.</summary>
    [Fact]
    public void A_part_with_no_balance_row_counts_as_nothing_in_stock()
        => SpareSalesEndpoints.ShortfallRefusal("SAL00099", OneLine, new Dictionary<long, int>())
            .Should().Contain("0 in warehouse stock");

    /// <summary>The whole point of the check: one pass names everything to fix. Reporting only the first
    /// short item is what the guarded decrement already did, and it meant editing and re-marking the sale
    /// once per missing part to find them all.</summary>
    [Fact]
    public void Every_short_item_is_named_not_just_the_first()
    {
        var wanted = new[]
        {
            (1L, "950000220", 5),
            (2L, "950000221", 2),
            (3L, "950000222", 9),
        };
        var onHand = new Dictionary<long, int> { [1L] = 1, [2L] = 2, [3L] = 0 };

        var refusal = SpareSalesEndpoints.ShortfallRefusal("SAL00099", wanted, onHand);

        refusal.Should().Contain("950000220").And.Contain("950000222");
        // The one the warehouse can cover is not listed as a problem.
        refusal.Should().NotContain("950000221");
    }

    /// <summary>The refusal has to say which sale, and what to do about it — it is read off a dialog by
    /// somebody standing at the counter with the customer in front of them.</summary>
    [Fact]
    public void The_refusal_names_the_sale_and_says_nothing_was_taken_out()
    {
        var refusal = SpareSalesEndpoints.ShortfallRefusal(
            "SAL00099", OneLine, new Dictionary<long, int> { [1L] = 0 });

        refusal.Should().Contain("SAL00099");
        refusal.Should().Contain("Nothing has been taken out");
        refusal.Should().Contain("Edit the sale");
    }
}
