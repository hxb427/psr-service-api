using ClosedXML.Excel;

namespace PSR.Service.Api.Reports;

/// <summary>Builds a simple one-sheet XLSX: bold frozen header row + string cells + auto width.</summary>
public static class XlsxBuilder
{
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
                        cell.Style.DateFormat.Format = "yyyy-mm-dd";
                        break;
                    case DateTime dt:
                        cell.Value = dt.AddHours(localOffsetHours);
                        cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
                        break;
                    case bool b: cell.Value = b ? "Yes" : "No"; break;
                    default: cell.Value = v.ToString(); break;
                }
            }
            r++;
        }

        ws.Columns().AdjustToContents(1, Math.Min(r, 200));   // sample-based autofit, cheap on big sheets
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
