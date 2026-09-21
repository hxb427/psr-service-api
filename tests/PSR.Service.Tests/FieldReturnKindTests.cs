using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// The two return kinds carry physically different things, and mixing them is the failure this split
/// exists to stop: a unit collected from a customer was never on the technician's balance, so
/// acknowledging it the good-stock way either fails on a balance they do not have, or quietly eats
/// stock they do have and books a broken unit onto the warehouse shelf as sellable.
///
/// These fix the membership of each list so a later edit cannot let one status drift across.
/// </summary>
public class FieldReturnKindTests
{
    [Fact]
    public void Good_stock_shipments_carry_only_units_still_held_as_issued()
    {
        SerialService.ShippableFor(StockReturnKind.GoodStock)
            .Should().Equal(SerialStatus.Received);
    }

    [Fact]
    public void Faulty_shipments_carry_only_units_taken_back_from_a_customer()
    {
        SerialService.ShippableFor(StockReturnKind.Faulty)
            .Should().BeEquivalentTo([SerialStatus.Collected, SerialStatus.Defective]);
    }

    /// <summary>A unit collected from a customer must never ride on a good-stock return: that path
    /// decrements the technician and credits the warehouse, and neither is true of this unit.</summary>
    [Theory]
    [InlineData(SerialStatus.Collected)]
    [InlineData(SerialStatus.Defective)]
    public void Collected_units_are_refused_on_a_good_stock_shipment(SerialStatus status)
    {
        SerialService.ShippableFor(StockReturnKind.GoodStock).Should().NotContain(status);
    }

    /// <summary>And the reverse: unused stock is not a repair job. Letting it in would open a job
    /// per unit for parts that are simply coming back to the shelf.</summary>
    [Fact]
    public void Unused_stock_is_refused_on_a_faulty_shipment()
    {
        SerialService.ShippableFor(StockReturnKind.Faulty).Should().NotContain(SerialStatus.Received);
    }

    /// <summary>Nothing in transit or already at the service center can be shipped again, whichever
    /// kind is claimed - otherwise one unit could be sent twice and counted twice.</summary>
    [Theory]
    [InlineData(SerialStatus.InTransitSc)]
    [InlineData(SerialStatus.InTransitTech)]
    [InlineData(SerialStatus.ReturnedToSc)]
    [InlineData(SerialStatus.UnderRepair)]
    [InlineData(SerialStatus.Repaired)]
    [InlineData(SerialStatus.Scrapped)]
    [InlineData(SerialStatus.Installed)]
    [InlineData(SerialStatus.Used)]
    [InlineData(SerialStatus.Issued)]
    [InlineData(SerialStatus.Missing)]
    public void No_kind_accepts_a_unit_that_is_not_physically_in_hand(SerialStatus status)
    {
        SerialService.ShippableFor(StockReturnKind.GoodStock).Should().NotContain(status);
        SerialService.ShippableFor(StockReturnKind.Faulty).Should().NotContain(status);
    }

    /// <summary>GoodStock is the default on the wire and in the column, which is what keeps every row
    /// written before faulty returns existed - and every client that does not send a kind - behaving
    /// exactly as it did.</summary>
    [Fact]
    public void Default_kind_is_good_stock()
    {
        default(StockReturnKind).Should().Be(StockReturnKind.GoodStock);
        new StockReturn().Kind.Should().Be(StockReturnKind.GoodStock);
    }
}
