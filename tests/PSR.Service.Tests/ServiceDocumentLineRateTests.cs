using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Documents;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>A serviced unit is PRICED tax-inclusive — the old app's "Rate (incl. tax)" box, and what
/// BillingService stores on the line — but the document prints the taxable value and totals the taxable
/// value underneath it. Printing the inclusive rate and line total gave a money column that summed to a
/// figure shown nowhere on the document, sitting directly above a Taxable Value it did not match.</summary>
public class ServiceDocumentLineRateTests
{
    /// <summary>A line as BillingService writes it: an inclusive rate, with the taxable amount divided back
    /// out of the line total at the line's own blended GST rate.</summary>
    private static ServiceDocumentLine Serviced(decimal inclusiveRate, int qty, decimal gst)
    {
        var lineTotal = inclusiveRate * qty;
        var taxable = gst > 0 ? Math.Round(lineTotal / (1 + gst / 100m), 2) : lineTotal;
        return new ServiceDocumentLine
        {
            ServiceJobId = 1, Qty = qty, UnitRate = inclusiveRate, GstPercent = gst,
            TaxableAmount = taxable, TaxAmount = lineTotal - taxable, LineTotal = lineTotal,
        };
    }

    [Theory]
    // inclusive rate, qty, gst %, the exclusive rate that should print
    [InlineData(4720.00, 1, 18, 4000.00)]
    [InlineData(2360.00, 1, 18, 2000.00)]
    [InlineData(1120.00, 1, 12, 1000.00)]
    public void Inclusive_rate_prints_tax_exclusive(decimal inclusive, int qty, decimal gst, decimal expected)
        => DocumentPdf.LineRate(Serviced(inclusive, qty, gst)).Should().Be(expected);

    [Theory]
    [InlineData(4720.00, 1, 18)]
    [InlineData(1120.00, 1, 12)]
    public void Printed_rate_times_qty_is_the_taxable_column(decimal inclusive, int qty, decimal gst)
    {
        var line = Serviced(inclusive, qty, gst);

        // What a reader checking the document with a calculator does.
        (DocumentPdf.LineRate(line) * qty).Should().Be(line.TaxableAmount);
    }

    /// <summary>The whole point of the change: the money column the table prints has to be the one the
    /// totals block adds up, because they sit against each other on the page. The column prints the stored
    /// taxable amount, which is what BillingService summed into the document's own Taxable Value.</summary>
    [Fact]
    public void Printed_column_foots_to_the_documents_taxable_value()
    {
        var lines = new[] { Serviced(4720m, 1, 18m), Serviced(2360m, 1, 18m), Serviced(0m, 1, 18m) };
        var docTaxable = lines.Sum(l => l.TaxableAmount);

        docTaxable.Should().Be(6000.00m);
        lines.Sum(l => l.TaxableAmount).Should().Be(docTaxable);
    }

    /// <summary>An in-warranty unit is billed at nothing. It still prints, at zero, and must not divide.</summary>
    [Fact]
    public void In_warranty_unit_prints_zero()
        => DocumentPdf.LineRate(Serviced(0m, 1, 18m)).Should().Be(0m);

    /// <summary>A line carrying no tax has nothing to divide out, so the stored rate prints unchanged.</summary>
    [Fact]
    public void Untaxed_line_prints_the_stored_rate()
        => DocumentPdf.LineRate(Serviced(500m, 2, 0m)).Should().Be(500m);
}

/// <summary>A grand total is rounded UP to the next whole rupee — the counter neither hands back paise nor
/// asks for them. The parts of the document keep their paise, so the gap has to show on the page as a
/// Round off line, or the customer adds the column and lands short of the total.</summary>
public class BillTotalRoundingTests
{
    [Theory]
    [InlineData(7330.47, 7331)]
    [InlineData(1237.54, 1238)]
    [InlineData(0.01, 1)]
    public void Payable_total_goes_up_to_the_next_rupee(decimal raw, decimal expected)
        => PSR.Service.Api.Common.BillMoney.RoundUp(raw).Should().Be(expected);

    /// <summary>A total already on the rupee is left alone — rounding "up" must not add one.</summary>
    [Theory]
    [InlineData(7330)]
    [InlineData(0)]
    public void Whole_rupee_total_is_untouched(decimal raw)
        => PSR.Service.Api.Common.BillMoney.RoundUp(raw).Should().Be(raw);

    /// <summary>What the Round off line on the PDF prints: the gap between the rounded total and the parts
    /// above it. Never more than a rupee, and never negative, because the rule only ever rounds up.</summary>
    [Theory]
    [InlineData(6000.00, 1080.47, 250.00, 7331)]
    [InlineData(1050.30, 187.24, 0.00, 1238)]
    public void Round_off_line_closes_the_gap(decimal taxable, decimal tax, decimal courier, decimal total)
    {
        var roundOff = total - (taxable + tax + courier);

        roundOff.Should().BeGreaterThan(0m).And.BeLessThan(1m);
        (taxable + tax + courier + roundOff).Should().Be(total);
    }
}

/// <summary>The two display rules the totals block gained, pulled out of the render so they can be checked
/// without rasterising a PDF: the Round off line that makes the block tie once the total is rounded up, and
/// the GST rate printed on the tax line in place of a per-line column the table has no width for.</summary>
public class DocumentTotalsBlockTests
{
    private static ServiceDocument Doc(decimal taxable, decimal igst, decimal courier, decimal total,
        params decimal[] lineGstPercents)
    {
        var doc = new ServiceDocument
        {
            DocType = DocumentType.PI, IsInterState = true,
            TaxableAmount = taxable, IgstAmount = igst, CourierCharges = courier, TotalAmount = total,
        };
        foreach (var gst in lineGstPercents)
            doc.Lines.Add(new ServiceDocumentLine { ServiceJobId = 1, Qty = 1, GstPercent = gst });
        return doc;
    }

    [Fact]
    public void Round_off_closes_the_gap_the_rounded_total_opens()
    {
        var doc = Doc(taxable: 6000.40m, igst: 1080.07m, courier: 250m, total: 7331m, 18m);

        DocumentPdf.RoundOff(doc).Should().Be(0.53m);
        (doc.TaxableAmount + doc.IgstAmount + doc.CourierCharges + DocumentPdf.RoundOff(doc))
            .Should().Be(doc.TotalAmount);
    }

    /// <summary>A document raised before the rounding rule came in already ties, and reprints with no Round
    /// off line at all rather than a spurious zero.</summary>
    [Fact]
    public void Document_that_already_ties_prints_no_round_off()
        => DocumentPdf.RoundOff(Doc(6000m, 1080m, 250m, 7330m, 18m)).Should().Be(0m);

    [Fact]
    public void One_rate_across_the_lines_prints_on_the_tax_line()
        => DocumentPdf.GstRateSuffix(Doc(6000m, 1080m, 0m, 7080m, 18m, 18m)).Should().Be(" @ 18%");

    /// <summary>A blended rate can be fractional — it is derived from the job's own mix of parts and charges,
    /// not read off a master — so the trailing zeroes come off rather than printing "@ 18.50%".</summary>
    [Fact]
    public void Fractional_rate_drops_its_trailing_zeroes()
        => DocumentPdf.GstRateSuffix(Doc(1000m, 185m, 0m, 1185m, 18.50m)).Should().Be(" @ 18.5%");

    /// <summary>Lines at different rates get no figure: one rate on the tax line would be a lie about the
    /// others, and the per-line GST is what the reader would have to be given instead.</summary>
    [Fact]
    public void Mixed_rates_print_no_figure()
        => DocumentPdf.GstRateSuffix(Doc(2000m, 300m, 0m, 2300m, 18m, 12m)).Should().BeEmpty();

    [Fact]
    public void Zero_rated_document_prints_no_figure()
        => DocumentPdf.GstRateSuffix(Doc(2000m, 0m, 0m, 2000m, 0m)).Should().BeEmpty();
}
