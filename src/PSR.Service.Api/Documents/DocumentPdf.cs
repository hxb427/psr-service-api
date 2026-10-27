using PSR.Service.Api.Data.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PSR.Service.Api.Documents;

/// <summary>Renders a <see cref="ServiceDocument"/> (one or more serviced units) to an A4 PDF. One layout serves
/// all three types — only the title and the GST summary differ. Table mirrors the legacy PI/Invoice layout
/// (a row per unit: Sr / Description / HSN / Serial / Warranty / Service Challan / Qty / Unit / Rate / Amount).
///
/// A delivery challan prices its units too. It covers in-warranty work, so nothing on it is being charged
/// for — the figures are the declared value of what is in the box, which is what transit needs and what a
/// customer receiving a free replacement signs against. A challan raised before that existed carries no
/// value at all, and reprints as it was issued: without the money columns rather than with a page of zeros.
/// A parts challan — spares going out to a dealer or a technician, with no job behind any line — reads
/// off the same guard from the other side: its lines are goods, so it prints the spare-sale table.
///
/// Both kinds of line print their money columns tax-EXCLUSIVE, so the last column adds up to the Taxable
/// Value in the totals block directly underneath it and the GST is added once, below the table. A serviced
/// unit is PRICED tax-inclusive — the old app's "Rate (incl. tax)" box, and what BillingService stores — so
/// its printed rate is derived back out; a spare sale already carries an exclusive rate. Printing the
/// inclusive figures instead gave a column that summed to nothing shown anywhere on the document.</summary>
public static class DocumentPdf
{
    public static byte[] Render(ServiceDocument doc, CompanyInfo company, string? watermark = null, string? sourcePiNo = null)
        => Build(doc, company, watermark, sourcePiNo).GeneratePdf();

    /// <summary>The laid-out document, before it is turned into a file. Split out from <see cref="Render"/>
    /// so the same layout can be rasterised to an image and looked at — QuestPDF embeds subset fonts, so a
    /// generated PDF cannot be read back as text and a wrong figure on the page is otherwise invisible to
    /// everything except a person opening it.</summary>
    internal static IDocument Build(ServiceDocument doc, CompanyInfo company, string? watermark = null,
        string? sourcePiNo = null)
    {
        var title = doc.DocType switch
        {
            DocumentType.PI => "Proforma Invoice",
            DocumentType.Invoice => "Tax Invoice",
            _ => "Delivery Challan",
        };
        var showMoney = ShowsMoney(doc);
        var isGoods = ShowsGoodsColumns(doc);
        // A per-line remark prints under its own description rather than in a column of its own. It is
        // optional and usually unused (a spare-sale line never has one), so a column for it spent width
        // on nothing most of the time — and with HSN and Unit now on the table there was no width spare
        // to spend. Under the description it reads against the line it belongs to and costs nothing when
        // it is absent.

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(t => t.FontSize(9).FontColor(Colors.Black));

                // Preview watermark — diagonal across the page, absent on the saved copy. Kept very pale
                // so it reads as a background wash rather than covering the figures being checked.
                if (!string.IsNullOrWhiteSpace(watermark))
                    page.Foreground().AlignCenter().AlignMiddle().Rotate(-45)
                        .Text(watermark).FontSize(46).Light().FontColor(Colors.Grey.Lighten2);

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text(company.Name).FontSize(15).Bold();
                            c.Item().Text(company.Address).FontSize(8);
                            c.Item().Text($"GSTIN: {company.Gstin}   State: {company.State} ({company.StateCode})").FontSize(8);
                        });
                        row.ConstantItem(190).Column(c =>
                        {
                            c.Item().AlignRight().Text(title).FontSize(14).Bold();
                            c.Item().AlignRight().Text($"No: {doc.DocNo}").FontSize(9).Bold();
                            // A tax invoice references the source proforma (PI No), like the old app.
                            if (doc.DocType == DocumentType.Invoice && !string.IsNullOrWhiteSpace(sourcePiNo))
                                c.Item().AlignRight().Text($"PI No: {sourcePiNo}").FontSize(9);
                            c.Item().AlignRight().Text($"Date: {doc.DocDate:dd-MMM-yyyy}").FontSize(9);
                        });
                    });
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Medium);
                });

                page.Content().PaddingVertical(8).Column(col =>
                {
                    // ---- party: Bill To (billing) + Consignee (delivery), like the old app ----
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("Bill To").FontSize(8).FontColor(Colors.Grey.Darken1);
                            c.Item().Text(doc.PartyName).Bold();
                            if (!string.IsNullOrWhiteSpace(doc.PartyAddress)) c.Item().Text(doc.PartyAddress).FontSize(8);
                            if (!string.IsNullOrWhiteSpace(doc.PartyGstin)) c.Item().Text($"GSTIN: {doc.PartyGstin}").FontSize(8);
                            if (!string.IsNullOrWhiteSpace(doc.PartyState))
                                c.Item().Text($"State: {doc.PartyState}  Code: {doc.PartyStateCode}").FontSize(8);
                        });
                        row.ConstantItem(16);
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("Consignee / Delivery").FontSize(8).FontColor(Colors.Grey.Darken1);
                            c.Item().Text(doc.PartyName).Bold();
                            c.Item().Text(string.IsNullOrWhiteSpace(doc.ConsigneeAddress) ? doc.PartyAddress : doc.ConsigneeAddress).FontSize(8);
                        });
                    });

                    // ---- unit table ----
                    col.Item().PaddingTop(8).Table(table =>
                    {
                        // Ten columns at their widest (a priced serviced document) against 539pt of usable
                        // A4 width, so every constant below is cut to the longest value it has to hold and
                        // the cell padding is 3 rather than 4 — ten columns pay for that twice over.
                        // Description takes whatever is left. QuestPDF throws instead of clipping when a
                        // row cannot fit, so widening one of these without taking the width off another
                        // fails the download rather than looking cramped.
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(18);    // sr
                            c.RelativeColumn(1);     // description (+ the line's remark under it)
                            c.ConstantColumn(46);    // hsn / sac
                            if (isGoods)
                            {
                                c.ConstantColumn(40);    // gst %
                            }
                            else
                            {
                                // Wide enough for a 15-character serial, for "OutOfWarranty" and for a
                                // full inward challan number on one line each — all three wrapped
                                // mid-value at narrower widths.
                                c.ConstantColumn(74);    // serial no
                                c.ConstantColumn(64);    // warranty
                                c.ConstantColumn(68);    // service challan
                            }
                            c.ConstantColumn(22);    // qty
                            c.ConstantColumn(30);    // unit
                            if (showMoney) { c.ConstantColumn(46); c.ConstantColumn(50); }   // rate, amount
                        });

                        table.Header(h =>
                        {
                            void Head(string text, bool right = false)
                            {
                                var cell = h.Cell().Background(Colors.Grey.Lighten3).Padding(3);
                                (right ? cell.AlignRight() : cell.AlignLeft()).Text(text).FontSize(8).Bold();
                            }
                            Head("#");
                            Head("Description");
                            Head("HSN/SAC");
                            if (isGoods) { Head("GST %", true); }
                            else { Head("Serial"); Head("Warranty"); Head("Service Challan"); }
                            Head("Qty", true);
                            Head("Unit");
                            if (showMoney) { Head("Rate", true); Head("Taxable", true); }
                        });

                        var i = 1;
                        foreach (var l in doc.Lines)
                        {
                            static IContainer Body(IContainer c) => c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3);
                            table.Cell().Element(Body).Text(i++.ToString());
                            table.Cell().Element(Body).Column(d =>
                            {
                                d.Item().Text(l.Description).FontSize(8);
                                if (!string.IsNullOrWhiteSpace(l.Remarks))
                                    d.Item().Text(l.Remarks).FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken1);
                            });
                            table.Cell().Element(Body).Text(l.HsnCode ?? "-").FontSize(8);
                            if (isGoods)
                            {
                                table.Cell().Element(Body).AlignRight().Text(l.GstPercent.ToString("0.##")).FontSize(8);
                            }
                            else
                            {
                                table.Cell().Element(Body).Text(l.SerialNo ?? "-").FontSize(8);
                                table.Cell().Element(Body).Text(l.Warranty ?? "-").FontSize(8);
                                table.Cell().Element(Body).Text(l.ServiceChallan ?? "-").FontSize(8);
                            }
                            table.Cell().Element(Body).AlignRight().Text(l.Qty.ToString());
                            table.Cell().Element(Body).Text(l.Unit ?? "-").FontSize(8);
                            if (showMoney)
                            {
                                table.Cell().Element(Body).AlignRight().Text(Num(LineRate(l)));
                                table.Cell().Element(Body).AlignRight().Text(Num(l.TaxableAmount));
                            }
                        }
                    });

                    // ---- totals (skipped for a delivery challan) ----
                    if (showMoney)
                        col.Item().PaddingTop(8).AlignRight().Width(230).Column(c =>
                        {
                            TotalRow(c, "Taxable Value", doc.TaxableAmount);
                            if (doc.IsInterState)
                            {
                                TotalRow(c, "IGST" + GstRateSuffix(doc), doc.IgstAmount);
                            }
                            else
                            {
                                // Half the line rate on each head, because that is how the one rate splits.
                                var half = GstRateSuffix(doc, 0.5m);
                                TotalRow(c, "CGST" + half, doc.CgstAmount);
                                TotalRow(c, "SGST" + half, doc.SgstAmount);
                            }
                            if (doc.CourierCharges > 0) TotalRow(c, CourierLabel(doc), doc.CourierCharges);

                            var roundOff = RoundOff(doc);
                            if (roundOff != 0) TotalRow(c, "Round off", roundOff);

                            c.Item().PaddingTop(2).BorderTop(1).BorderColor(Colors.Grey.Medium);
                            c.Item().Row(r =>
                            {
                                r.RelativeItem().Text("Total Amount").Bold();
                                r.ConstantItem(110).AlignRight().Text(Money(doc.TotalAmount)).Bold();
                            });
                        });

                    if (!string.IsNullOrWhiteSpace(doc.CourierMode))
                        col.Item().PaddingTop(6).Text($"Courier: {doc.CourierMode}").FontSize(8);
                    if (!string.IsNullOrWhiteSpace(doc.Remarks))
                        col.Item().PaddingTop(4).Text($"Remarks: {doc.Remarks}").FontSize(8);

                    // Authorized signature block (matches the legacy document layout).
                    col.Item().PaddingTop(34).AlignRight().Width(220).Column(c =>
                    {
                        c.Item().Text($"for {company.Name}").FontSize(9);
                        c.Item().PaddingTop(28).LineHorizontal(0.75f).LineColor(Colors.Grey.Medium);
                        c.Item().AlignCenter().Text("Authorized Signature").FontSize(8).FontColor(Colors.Grey.Darken1);
                    });
                });

                page.Footer().Column(col =>
                {
                    col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                    col.Item().PaddingTop(4).AlignCenter()
                        .Text("This is a computer-generated document.").FontSize(7).FontColor(Colors.Grey.Darken1).Italic();
                });
            });
        });
    }

    /// <summary>Whether the table prints the GOODS column set — HSN and a GST % — rather than the
    /// serial / warranty / inward-challan trio a serviced unit carries. The two sets are mutually
    /// exclusive because there is only so much A4 to go round.
    ///
    /// Read off the lines, not off <see cref="ServiceDocument.SpareSaleId"/>. A spare sale is one source
    /// of catalogue lines and a parts delivery challan — spares going out to a dealer or a technician,
    /// billing no sale at all — is the other, and on the challan the trio would print three columns of
    /// dashes where the tax rate should be. Every line of a service document carries its job, and every
    /// line of the other two carries none, so the lines themselves say which document this is.</summary>
    public static bool ShowsGoodsColumns(ServiceDocument doc)
        => doc.Lines.Count > 0 && doc.Lines.All(l => l.ServiceJobId is null);

    /// <summary>Whether a document prints its money columns and its totals block.
    ///
    /// A delivery challan is priced like the other two now — it covers in-warranty work, so nothing on it
    /// is being charged for and the figures are the declared value of what is in the box. The guard is on
    /// the figures rather than on the type, so a challan issued before that existed, or one deliberately
    /// sent out with nothing declared, prints as it always did instead of growing a page of zeros under a
    /// Total Amount of Rs. 0.00.</summary>
    public static bool ShowsMoney(ServiceDocument doc) => doc.DocType != DocumentType.DC || doc.TotalAmount > 0;

    /// <summary>What the courier row in the totals block is labelled. The charge carries its own SAC, which
    /// the table cannot show it under because it sits on the document rather than on any line of it — so it
    /// goes on the label. Read off the document rather than from the constant, so a challan raised before
    /// the code was stamped reprints without one instead of claiming a code it never carried.</summary>
    public static string CourierLabel(ServiceDocument doc) => string.IsNullOrWhiteSpace(doc.CourierHsnCode)
        ? "Courier"
        : $"Courier (HSN {doc.CourierHsnCode})";

    /// <summary>The tax-exclusive rate to print for a line, so Rate × Qty is exactly the Taxable column
    /// beside it. The stored rate is used as-is when it already reproduces the stored taxable amount, which
    /// is a spare-sale line and any line at 0% GST; otherwise the rate is derived from the taxable amount.
    ///
    /// That covers both a serviced unit, which is priced tax-inclusive by design, and the spare-sale
    /// documents generated before the exclusive convention, which snapshotted an inclusive rate — a tax
    /// invoice gets reprinted years later and still has to add up.</summary>
    public static decimal LineRate(ServiceDocumentLine l)
        => l.Qty > 0 && Math.Round(l.UnitRate * l.Qty, 2) != l.TaxableAmount
            ? Math.Round(l.TaxableAmount / l.Qty, 2)
            : l.UnitRate;

    /// <summary>What the Round off line prints, or zero when there is nothing to print. The total is rounded
    /// up to the whole rupee that actually gets collected while the parts above it keep their paise, so the
    /// gap has to be shown or the column does not add up to the figure under it.
    ///
    /// Derived from the document rather than stored on it, which also means a document raised before the
    /// rounding rule came in reproduces exactly as it was issued, with no line at all.</summary>
    public static decimal RoundOff(ServiceDocument doc) => doc.TotalAmount
        - (doc.TaxableAmount + doc.CgstAmount + doc.SgstAmount + doc.IgstAmount + doc.CourierCharges);

    /// <summary>" @ 18%" for the tax line, when every line on the document carries the same GST rate.
    ///
    /// The rate goes here rather than into a column of its own: a service table has no width left beside the
    /// serial, warranty and challan columns, and the money columns are printed tax-exclusive, which only says
    /// something against a rate shown somewhere. Blank when the lines disagree — one figure would be a lie —
    /// and blank at 0%, where there is no tax line worth qualifying.</summary>
    /// <param name="share">The part of the rate this line carries. An intra-state document splits the
    /// same rate over two heads, so CGST and SGST each print half of it: 18% GST is 9% + 9%.</param>
    public static string GstRateSuffix(ServiceDocument doc, decimal share = 1m)
    {
        var rates = doc.Lines.Select(l => l.GstPercent).Distinct().ToList();
        return rates.Count == 1 && rates[0] > 0 ? $" @ {rates[0] * share:0.##}%" : "";
    }

    private static void TotalRow(ColumnDescriptor c, string label, decimal value)
        => c.Item().Row(r =>
        {
            r.RelativeItem().Text(label).FontSize(9);
            r.ConstantItem(110).AlignRight().Text(Money(value)).FontSize(9);
        });

    private static string Money(decimal v) => "Rs. " + v.ToString("N2");

    /// <summary>A money figure inside the table, with no currency on it. There are eleven columns to fit
    /// across A4 now, and "Rs." repeated down two of them cost more width than it carried: the totals block
    /// sits directly under the last column and prints the currency, under a letterhead carrying the GSTIN.</summary>
    private static string Num(decimal v) => v.ToString("N2");
}
