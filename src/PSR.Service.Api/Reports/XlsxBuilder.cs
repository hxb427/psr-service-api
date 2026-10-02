using ClosedXML.Excel;

namespace PSR.Service.Api.Reports;

/// <summary>Builds a simple one-sheet XLSX: bold frozen header row + string cells + auto width.</summary>
public static class XlsxBuilder
{
    private const string DateFormat = "yyyy-mm-dd";
    private const string TimestampFormat = "yyyy-mm-dd hh:mm";

    /// <summary>What each date format needs to render, in the character units Excel measures column
    /// width in: the format's own length plus a little padding. A date format has no fallback — too
    /// narrow and the cell shows "####" rather than a shortened date — and its width is fixed, so
    /// the figure is exact rather than a guess.</summary>
    private const double DateColumnWidth = 11;
    private const double TimestampColumnWidth = 17.5;

    /// <param name="rows">One list of values per data row, each the same length as
    /// <paramref name="headers"/> and in the same order.</param>
    /// <param name="localOffsetHours">Hours to add to every timestamp before it is written. The API
    /// stores and works in UTC, but a spreadsheet has nowhere to record a zone — the cell is a bare
    /// wall-clock number — so an unconverted export reads five and a half hours behind the shop that
    /// asked for it. Zero for sheets carrying no dates.</param>
    public static byte[] Build(string sheetName, IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<object?>> rows, double localOffsetHours = 0)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(sheetName);

        for (var c = 0; c < headers.Count; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);

        // The width each column needs for the widest date format written into it, by column number.
        var dateColumnWidths = new Dictionary<int, double>();

        var r = 2;
        foreach (var row in rows)
        {
            // Every caller builds the headers and the values as two separate array literals, so a
            // column added to one and not the other is a one-character mistake that writes a
            // perfectly valid spreadsheet with every value after the gap sitting under the wrong
            // heading. Nobody reading it would know. A failed export says so; a wrong one does not.
            if (row.Count != headers.Count)
                throw new ArgumentException(
                    $"The \"{sheetName}\" sheet has {headers.Count} column(s), but data row {r - 1} " +
                    $"carries {row.Count} value(s). The headers and the values are built separately " +
                    "and have to be kept in step — a row that does not line up files its values " +
                    "under the wrong headings.", nameof(rows));

            for (var c = 0; c < row.Count; c++)
            {
                var v = row[c];
                var cell = ws.Cell(r, c + 1);
                switch (v)
                {
                    case null: break;
                    case int i: cell.Value = i; break;
                    case long l: cell.Value = l; break;
                    case decimal d: cell.Value = d; break;
                    case double db: cell.Value = db; break;
                    // A timestamp and a calendar date both arrive as DateTime and need opposite
                    // treatment: an instant has to be moved onto the shop's clock to read correctly,
                    // a received date must NOT be moved or it lands at 05:30 — or, an hour either
                    // side of midnight, on the wrong day. Midnight is the tell, because a business
                    // date is stored as the day itself. A timestamp landing exactly on 00:00:00.000
                    // is printed as a bare date, which is the harmless way to be wrong.
                    case DateTime dt when dt.TimeOfDay == TimeSpan.Zero:
                        cell.Value = dt;
                        cell.Style.DateFormat.Format = DateFormat;
                        NeedsWidth(c + 1, DateColumnWidth);
                        break;
                    case DateTime dt:
                        cell.Value = dt.AddHours(localOffsetHours);
                        cell.Style.DateFormat.Format = TimestampFormat;
                        NeedsWidth(c + 1, TimestampColumnWidth);
                        break;
                    case bool b: cell.Value = b ? "Yes" : "No"; break;
                    default: cell.Value = v.ToString(); break;
                }
            }
            r++;
        }

        ws.Columns().AdjustToContents(1, Math.Min(r, 200));   // sample-based autofit, cheap on big sheets

        // The autofit only measured the rows it sampled, so a column whose first value falls past
        // the sample was measured empty and sized to its heading. A text column merely looks
        // cramped; a date column narrower than its own format renders as "####", and the date is
        // unreadable until someone widens it by hand. Several columns hit this reliably rather than
        // by luck — the register exports newest-first, and a job booked in this morning has no
        // dispatched, stocked, replaced or written-off date yet, so those columns are empty for as
        // far down as the sample reaches. Flooring them costs nothing and needs no wider sample,
        // because a date format's width does not depend on the value.
        foreach (var (column, width) in dateColumnWidths)
            if (ws.Column(column).Width < width)
                ws.Column(column).Width = width;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();

        void NeedsWidth(int column, double width)
        {
            if (!dateColumnWidths.TryGetValue(column, out var seen) || width > seen)
                dateColumnWidths[column] = width;
        }
    }
}
