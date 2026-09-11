using ClosedXML.Excel;

namespace PSR.Service.Api.Reports;

/// <summary>Builds a simple one-sheet XLSX: bold frozen header row + string cells + auto width.</summary>
public static class XlsxBuilder
{
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
