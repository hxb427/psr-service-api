using PSR.Service.Api.Data.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PSR.Service.Api.Documents;

/// <summary>Renders a <see cref="ServiceDocument"/> (one or more serviced units) to an A4 PDF. One layout serves
/// all three types — only the title and the GST summary differ. Table mirrors the legacy PI/Invoice layout
/// (a row per unit: Sr / Description / Serial / Warranty / Service Challan / Qty / Rate / Amount).
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
        var showMoney = doc.DocType != DocumentType.DC;   // a delivery challan lists units without pricing
        // A spare sale bills catalogue items, so warranty and inward-challan columns are meaningless —
        // it prints HSN instead, which a serviced unit doesn't carry.
        var isSale = doc.SpareSaleId is not null;
        // Per-line remarks are optional and usually unused (a spare-sale line never has any), and an
        // empty column still costs the width the description and the serial want. Dropped unless at
        // least one line actually says something.
        var showRemarks = doc.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Remarks));

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
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(24);    // sr
                            c.RelativeColumn(3);     // description
                            if (isSale)
                            {
                                c.ConstantColumn(56);    // hsn
                                c.ConstantColumn(44);    // gst %
                            }
                            else
                            {
                                // Wide enough for a 15-character serial and for "OutOfWarranty" on one
                                // line — both of them wrapped mid-word at the old widths.
                                c.ConstantColumn(84);    // serial no
                                c.ConstantColumn(68);    // warranty
                                c.ConstantColumn(64);    // service challan
                            }
                            c.ConstantColumn(26);    // qty
                            if (showRemarks) c.RelativeColumn(1);
                            if (showMoney) { c.ConstantColumn(58); c.ConstantColumn(64); }   // rate, amount
                        });

                        table.Header(h =>
                        {
                            void Head(string text, bool right = false)
                            {
                                var cell = h.Cell().Background(Colors.Grey.Lighten3).Padding(4);
                                (right ? cell.AlignRight() : cell.AlignLeft()).Text(text).FontSize(8).Bold();
                            }
                            Head("#");
                            Head("Description");
                            if (isSale) { Head("HSN"); Head("GST %", true); }
                            else { Head("Serial"); Head("Warranty"); Head("Service Challan"); }
                            Head("Qty", true);
                            if (showRemarks) Head("Remarks");
                            if (showMoney) { Head("Rate", true); Head("Taxable", true); }
                        });

                        var i = 1;
                        foreach (var l in doc.Lines)
                        {
                            static IContainer Body(IContainer c) => c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4);
                            table.Cell().Element(Body).Text(i++.ToString());
                            table.Cell().Element(Body).Text(l.Description).FontSize(8);
                            if (isSale)
                            {
                                table.Cell().Element(Body).Text(l.HsnCode ?? "-").FontSize(8);
                                table.Cell().Element(Body).AlignRight().Text(l.GstPercent.ToString("0.##")).FontSize(8);
                            }
                            else
                            {
                                table.Cell().Element(Body).Text(l.SerialNo ?? "-").FontSize(8);
                                table.Cell().Element(Body).Text(l.Warranty ?? "-").FontSize(8);
                                table.Cell().Element(Body).Text(l.ServiceChallan ?? "-").FontSize(8);
                            }
                            table.Cell().Element(Body).AlignRight().Text(l.Qty.ToString());
                            if (showRemarks) table.Cell().Element(Body).Text(l.Remarks ?? "").FontSize(8);
                            if (showMoney)
                            {
                                table.Cell().Element(Body).AlignRight().Text(Money(LineRate(l)));
                                table.Cell().Element(Body).AlignRight().Text(Money(l.TaxableAmount));
                            }
                        }
                    });

                    // ---- totals (skipped for a delivery challan) ----
                    if (showMoney)
                        col.Item().PaddingTop(8).AlignRight().Width(230).Column(c =>
                        {
                            TotalRow(c, "Taxable Value", doc.TaxableAmount);
                            if (doc.IsInterState) TotalRow(c, "IGST" + GstRateSuffix(doc), doc.IgstAmount);
                            else { TotalRow(c, "CGST", doc.CgstAmount); TotalRow(c, "SGST", doc.SgstAmount); }
                            if (doc.CourierCharges > 0) TotalRow(c, "Courier", doc.CourierCharges);

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
    public static string GstRateSuffix(ServiceDocument doc)
    {
        var rates = doc.Lines.Select(l => l.GstPercent).Distinct().ToList();
        return rates.Count == 1 && rates[0] > 0 ? $" @ {rates[0]:0.##}%" : "";
    }

    private static void TotalRow(ColumnDescriptor c, string label, decimal value)
        => c.Item().Row(r =>
        {
            r.RelativeItem().Text(label).FontSize(9);
            r.ConstantItem(110).AlignRight().Text(Money(value)).FontSize(9);
        });

    private static string Money(decimal v) => "Rs. " + v.ToString("N2");
}
