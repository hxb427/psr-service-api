using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Documents;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>A spare-sale document prints Rate, Qty, GST % and Taxable side by side, so the three have to
/// agree: Rate × Qty must be exactly the Taxable column, and that column must sum to the Taxable Value in
/// the totals block the GST is then added to. It did not, because the rate was derived by dividing the
/// tax-inclusive line total by the quantity and rounding — which loses paise, and also left a GST %
/// printed against a rate the tax had already been added to.</summary>
public class SaleDocumentLineRateTests
{
    private static ServiceDocumentLine Line(decimal unitRate, int qty, decimal taxable) =>
        new() { PartId = 1, Qty = qty, UnitRate = unitRate, TaxableAmount = taxable };

    [Theory]
    // rate excl, qty, taxable  — the rate a current document stores reproduces its taxable amount
    [InlineData(10.10, 3, 30.30)]
    [InlineData(100.00, 1, 100.00)]
    [InlineData(0.05, 7, 0.35)]
    public void Stored_exclusive_rate_is_printed_as_is(decimal rate, int qty, decimal taxable)
        => DocumentPdf.SaleLineRate(Line(rate, qty, taxable)).Should().Be(rate);

    [Theory]
    [InlineData(10.10, 3, 30.30)]
    [InlineData(100.00, 1, 100.00)]
    [InlineData(0.05, 7, 0.35)]
    public void Printed_rate_times_qty_is_the_taxable_column(decimal rate, int qty, decimal taxable)
    {
        var printed = DocumentPdf.SaleLineRate(Line(rate, qty, taxable));

        // What a reader checking the document with a calculator does.
        (printed * qty).Should().Be(taxable);
    }

    /// <summary>Documents raised before the convention changed snapshotted a tax-inclusive rate. Those
    /// are spotted by the rate not reproducing the stored taxable amount, and reprint from the taxable
    /// amount instead — a tax invoice gets reprinted years later and still has to add up.</summary>
    [Fact]
    public void Legacy_inclusive_rate_reprints_from_the_taxable_amount()
    {
        // The row the old code wrote for qty 3 @ 10.10 excl + 12% GST: round(33.94 / 3, 2).
        var legacy = Line(unitRate: 11.31m, qty: 3, taxable: 30.30m);

        var printed = DocumentPdf.SaleLineRate(legacy);

        printed.Should().Be(10.10m);
        (printed * legacy.Qty).Should().Be(legacy.TaxableAmount);
    }

    /// <summary>A zero quantity should never reach a saved line, but dividing by it would be a 500 on a
    /// PDF download rather than a wrong number, so the rule leaves the stored rate alone.</summary>
    [Fact]
    public void Zero_quantity_falls_back_to_the_stored_rate()
        => DocumentPdf.SaleLineRate(Line(42m, 0, 0m)).Should().Be(42m);
}
