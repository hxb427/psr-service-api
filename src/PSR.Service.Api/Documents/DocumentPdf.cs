using PSR.Service.Api.Data.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PSR.Service.Api.Documents;

/// <summary>Renders a <see cref="ServiceDocument"/> (one or more serviced units) to an A4 PDF. One layout serves
/// all three types — only the title and the GST summary differ. Table mirrors the legacy PI/Invoice layout
/// (a row per unit: Sr / Description / Warranty / Service Challan / Qty / Rate / Amount).</summary>
public static class DocumentPdf
{
    public static byte[] Render(ServiceDocument doc, CompanyInfo company, string? watermark = null, string? sourcePiNo = null)
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

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(t => t.FontSize(9).FontColor(Colors.Black));

                // Preview watermark — drawn diagonally across the whole page; absent on the saved/clean copy.
                if (!string.IsNullOrWhiteSpace(watermark))
                    page.Foreground().AlignCenter().AlignMiddle().Rotate(-45)
                        .Text(watermark).FontSize(52).Bold().FontColor(Colors.Red.Lighten3);

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
                                c.ConstantColumn(58);    // warranty
                                c.ConstantColumn(64);    // service challan
                            }
                            c.ConstantColumn(28);    // qty
                            c.RelativeColumn(1.4f);  // remarks
                            if (showMoney) { c.ConstantColumn(60); c.ConstantColumn(66); }   // rate, amount
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
                            else { Head("Warranty"); Head("Service Challan"); }
                            Head("Qty", true);
                            Head("Remarks");
                            if (showMoney) { Head("Rate", true); Head("Amount", true); }
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
                                table.Cell().Element(Body).Text(l.Warranty ?? "-").FontSize(8);
                                table.Cell().Element(Body).Text(l.ServiceChallan ?? "-").FontSize(8);
                            }
                            table.Cell().Element(Body).AlignRight().Text(l.Qty.ToString());
                            table.Cell().Element(Body).Text(l.Remarks ?? "").FontSize(8);
                            if (showMoney)
                            {
                                table.Cell().Element(Body).AlignRight().Text(Money(l.UnitRate));
                                table.Cell().Element(Body).AlignRight().Text(Money(l.LineTotal));
                            }
                        }
                    });

                    // ---- totals (skipped for a delivery challan) ----
                    if (showMoney)
                        col.Item().PaddingTop(8).AlignRight().Width(230).Column(c =>
                        {
                            TotalRow(c, "Taxable Value", doc.TaxableAmount);
                            if (doc.IsInterState) TotalRow(c, "IGST", doc.IgstAmount);
                            else { TotalRow(c, "CGST", doc.CgstAmount); TotalRow(c, "SGST", doc.SgstAmount); }
                            if (doc.CourierCharges > 0) TotalRow(c, "Courier", doc.CourierCharges);
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
        }).GeneratePdf();
    }

    private static void TotalRow(ColumnDescriptor c, string label, decimal value)
        => c.Item().Row(r =>
        {
            r.RelativeItem().Text(label).FontSize(9);
            r.ConstantItem(110).AlignRight().Text(Money(value)).FontSize(9);
        });

    private static string Money(decimal v) => "Rs. " + v.ToString("N2");
}
