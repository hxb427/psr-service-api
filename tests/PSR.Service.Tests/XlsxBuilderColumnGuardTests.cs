using ClosedXML.Excel;
using FluentAssertions;
using PSR.Service.Api.Reports;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Every export in ReportsEndpoints builds its header row and its value row as two separate
/// array literals, several of them 30 columns wide and wrapped over four lines. Adding a column to one
/// and forgetting the other produces a spreadsheet that opens cleanly with every value after the gap
/// filed under the wrong heading — a dispatch date sitting under "Written off", say. Nothing fails and
/// nothing looks wrong, which is the problem: the sheet is read as fact.
///
/// So the builder refuses a row that does not line up. These tests hold it to that, and read a good
/// workbook back cell by cell to confirm the alignment it promises is the alignment it writes.</summary>
public class XlsxBuilderColumnGuardTests
{
    private static readonly string[] Headers = { "Service no", "Received", "Dispatched", "Remarks" };

    private static IEnumerable<IReadOnlyList<object?>> OneRow(params object?[] values)
        => new[] { (IReadOnlyList<object?>)values };

    [Fact]
    public void A_row_matching_the_headers_lands_under_them()
    {
        var bytes = XlsxBuilder.Build("Service records", Headers,
            OneRow("SVC00042", new DateTime(2026, 9, 14), new DateTime(2026, 9, 20, 11, 30, 0), "Display replaced"));

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet(1);

        ws.Cell(1, 1).GetString().Should().Be("Service no");
        ws.Cell(1, 3).GetString().Should().Be("Dispatched");
        ws.Cell(2, 1).GetString().Should().Be("SVC00042");
        ws.Cell(2, 3).GetDateTime().Should().Be(new DateTime(2026, 9, 20, 11, 30, 0));
        ws.Cell(2, 4).GetString().Should().Be("Display replaced");
    }

    [Fact]
    public void A_row_short_of_a_value_is_refused()
    {
        // The shape of a forgotten column: three values under four headings, so "Remarks" reads empty
        // and the sheet is quietly wrong rather than quietly short.
        var build = () => XlsxBuilder.Build("Service records", Headers,
            OneRow("SVC00042", new DateTime(2026, 9, 14), "Display replaced"));

        build.Should().Throw<ArgumentException>()
            .WithMessage("*Service records*")
            .WithMessage("*4 column(s)*")
            .WithMessage("*3 value(s)*");
    }

    [Fact]
    public void A_row_carrying_a_value_too_many_is_refused()
    {
        var build = () => XlsxBuilder.Build("Service records", Headers,
            OneRow("SVC00042", new DateTime(2026, 9, 14), new DateTime(2026, 9, 20), "Display replaced", "SN-1"));

        build.Should().Throw<ArgumentException>().WithMessage("*5 value(s)*");
    }

    [Fact]
    public void The_offending_row_is_named_so_a_ragged_export_can_be_found()
    {
        // A mismatch that only some rows have is the worst case to debug — one null coalesced into a
        // shorter array, say — so the message counts data rows, not worksheet rows.
        IEnumerable<IReadOnlyList<object?>> rows = new[]
        {
            (IReadOnlyList<object?>)new object?[] { "SVC00001", null, null, null },
            new object?[] { "SVC00002", null, null, null },
            new object?[] { "SVC00003", null },
        };

        var build = () => XlsxBuilder.Build("Service records", Headers, rows);

        build.Should().Throw<ArgumentException>().WithMessage("*data row 3*");
    }

    [Fact]
    public void An_export_with_no_rows_is_still_a_sheet_with_headings()
    {
        var bytes = XlsxBuilder.Build("Service records", Headers, []);

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        wb.Worksheet(1).Cell(1, 4).GetString().Should().Be("Remarks");
        wb.Worksheet(1).Cell(2, 1).IsEmpty().Should().BeTrue();
    }
}
