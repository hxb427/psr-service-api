using ClosedXML.Excel;
using FluentAssertions;
using PSR.Service.Api.Reports;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Column widths are autofit from a 200-row sample, which is deliberate — measuring every
/// cell of a 10,000-row export is slow. The catch is that a column whose first value falls past the
/// sample gets measured empty and sized to its heading, and a date narrower than its own format does
/// not shorten: Excel fills the cell with "####" and the date cannot be read until someone widens the
/// column by hand.
///
/// The register hits this reliably rather than by luck. It exports newest-first, and a job booked in
/// this morning has no dispatched, stocked, replaced or written-off date yet, so every closing-date
/// column is empty for further down than the sample reaches. PI date and DC date were landing the
/// same way. So the builder floors each date column at the width its format needs.</summary>
public class XlsxBuilderDateColumnWidthTests
{
    private const int PastTheAutofitSample = 240;

    /// <summary>The register's shape: a date column that is empty until well past row 200.</summary>
    private static IEnumerable<IReadOnlyList<object?>> RowsWithALateDate(DateTime date)
    {
        for (var i = 1; i <= 250; i++)
            yield return new object?[] { $"SVC{i:00000}", i == PastTheAutofitSample ? date : null };
    }

    private static IXLWorksheet Sheet(byte[] bytes) => new XLWorkbook(new MemoryStream(bytes)).Worksheet(1);

    [Fact]
    public void A_timestamp_first_appearing_past_the_sample_still_has_room_to_render()
    {
        var bytes = XlsxBuilder.Build("Service records", new[] { "Service no", "Dispatched" },
            RowsWithALateDate(new DateTime(2026, 9, 20, 14, 35, 0)));

        var ws = Sheet(bytes);

        ws.Cell(PastTheAutofitSample + 1, 2).GetDateTime().Should().Be(new DateTime(2026, 9, 20, 14, 35, 0));
        // "yyyy-mm-dd hh:mm" is 16 characters; anything less renders as ####.
        ws.Column(2).Width.Should().BeGreaterThanOrEqualTo(16);
    }

    [Fact]
    public void A_bare_date_first_appearing_past_the_sample_still_has_room_to_render()
    {
        // PI date and DC date: a business date, written without a time.
        var bytes = XlsxBuilder.Build("Service records", new[] { "Service no", "PI date" },
            RowsWithALateDate(new DateTime(2026, 9, 20)));

        Sheet(bytes).Column(2).Width.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void A_date_column_is_not_narrowed_when_the_autofit_already_made_it_wider()
    {
        // A long heading is measured by the autofit and must survive the floor, or the heading is
        // the thing that gets clipped instead.
        var bytes = XlsxBuilder.Build("Service records",
            new[] { "Service no", "Service completed, to the minute, in shop time" },
            RowsWithALateDate(new DateTime(2026, 9, 20, 14, 35, 0)));

        Sheet(bytes).Column(2).Width.Should().BeGreaterThan(40);
    }

    [Fact]
    public void A_column_carrying_no_date_is_left_to_the_autofit()
    {
        var bytes = XlsxBuilder.Build("Held items", new[] { "Technician", "On hand" },
            new[] { (IReadOnlyList<object?>)new object?[] { "ravi", 3 } });

        // Nothing here needs date room; widening a short numeric column would only waste the page.
        Sheet(bytes).Column(2).Width.Should().BeLessThan(16);
    }
}
