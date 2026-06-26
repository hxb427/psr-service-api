using PSR.Service.Api.Data.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PSR.Service.Api.Documents;

/// <summary>Renders a <see cref="ServiceDocument"/> to a single-page A4 PDF. One layout serves all three
/// document types — only the title and the GST summary differ.</summary>
public static class DocumentPdf
{
    public static byte[] Render(ServiceDocument doc, CompanyInfo company,
        string? serviceNo, string? serial, string? model)
    {
        var title = doc.DocType switch
        {
            DocumentType.PI => "PROFORMA INVOICE",
            DocumentType.Invoice => "TAX INVOICE",
            _ => "DELIVERY CHALLAN",
        };

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(t => t.FontSize(9).FontColor(Colors.Black));

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text(company.Name).FontSize(15).Bold();
                            c.Item().Text(company.Address).FontSize(8);
                            c.Item().Text($"GSTIN: {company.Gstin}   State: {company.State} ({company.StateCode})").FontSize(8);
                            if (!string.IsNullOrWhiteSpace(company.Phone) || !string.IsNullOrWhiteSpace(company.Email))
                                c.Item().Text($"{company.Phone}  {company.Email}".Trim()).FontSize(8);
                        });
                        row.ConstantItem(170).Column(c =>
                        {
                            c.Item().AlignRight().Text(title).FontSize(14).Bold();
                            c.Item().AlignRight().Text($"No: {doc.DocNo}").FontSize(9).Bold();
                            c.Item().AlignRight().Text($"Date: {doc.DocDate:dd-MMM-yyyy}").FontSize(9);
                        });
                    });
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Medium);
                });

                page.Content().PaddingVertical(8).Column(col =>
                {
                    // ---- party + service reference ----
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("Bill To").FontSize(8).FontColor(Colors.Grey.Darken1);
                            c.Item().Text(doc.PartyName).Bold();
                            if (!string.IsNullOrWhiteSpace(doc.PartyAddress)) c.Item().Text(doc.PartyAddress).FontSize(8);
                            if (!string.IsNullOrWhiteSpace(doc.PartyGstin)) c.Item().Text($"GSTIN: {doc.PartyGstin}").FontSize(8);
                            if (!string.IsNullOrWhiteSpace(doc.PartyState))
                                c.Item().Text($"State: {doc.PartyState} ({doc.PartyStateCode})").FontSize(8);
                        });
                        row.ConstantItem(190).Column(c =>
                        {
                            if (!string.IsNullOrWhiteSpace(serviceNo)) c.Item().Text($"Service No: {serviceNo}").FontSize(8);
                            if (!string.IsNullOrWhiteSpace(serial)) c.Item().Text($"Serial No: {serial}").FontSize(8);
                            if (!string.IsNullOrWhiteSpace(model)) c.Item().Text($"Model: {model}").FontSize(8);
                            if (!string.IsNullOrWhiteSpace(doc.CourierMode)) c.Item().Text($"Courier: {doc.CourierMode}").FontSize(8);
                        });
                    });

                    // ---- line table ----
                    col.Item().PaddingTop(8).Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(20);    // #
                            c.RelativeColumn(3);     // description
                            c.ConstantColumn(50);    // HSN
                            c.ConstantColumn(28);    // qty
                            c.ConstantColumn(58);    // rate
                            c.ConstantColumn(62);    // taxable
                            c.ConstantColumn(34);    // gst %
                            c.ConstantColumn(58);    // tax
                            c.ConstantColumn(64);    // amount
                        });

                        table.Header(h =>
                        {
                            void HeadCell(string text, bool right = false)
                            {
                                var cell = h.Cell().Background(Colors.Grey.Lighten3).Padding(4);
                                (right ? cell.AlignRight() : cell.AlignLeft()).Text(text).FontSize(8).Bold();
                            }
                            HeadCell("#");
                            HeadCell("Description");
                            HeadCell("HSN");
                            HeadCell("Qty", true);
                            HeadCell("Rate", true);
                            HeadCell("Taxable", true);
                            HeadCell("GST%", true);
                            HeadCell("Tax", true);
                            HeadCell("Amount", true);
                        });

                        var i = 1;
                        foreach (var l in doc.Lines)
                        {
                            static IContainer Body(IContainer c) => c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4);
                            table.Cell().Element(Body).Text(i++.ToString());
                            table.Cell().Element(Body).Text(l.Description).FontSize(8);
                            table.Cell().Element(Body).Text(l.HsnCode ?? "-").FontSize(8);
                            table.Cell().Element(Body).AlignRight().Text(l.Qty.ToString());
                            table.Cell().Element(Body).AlignRight().Text(Money(l.UnitRate));
                            table.Cell().Element(Body).AlignRight().Text(Money(l.TaxableAmount));
                            table.Cell().Element(Body).AlignRight().Text($"{l.GstPercent:0.##}");
                            table.Cell().Element(Body).AlignRight().Text(Money(l.TaxAmount));
                            table.Cell().Element(Body).AlignRight().Text(Money(l.LineTotal));
                        }
                    });

                    // ---- totals ----
                    col.Item().PaddingTop(8).AlignRight().Width(230).Column(c =>
                    {
                        TotalRow(c, "Taxable", doc.TaxableAmount);
                        if (doc.IsInterState)
                            TotalRow(c, "IGST", doc.IgstAmount);
                        else
                        {
                            TotalRow(c, "CGST", doc.CgstAmount);
                            TotalRow(c, "SGST", doc.SgstAmount);
                        }
                        if (doc.CourierCharges > 0) TotalRow(c, "Courier", doc.CourierCharges);
                        c.Item().PaddingTop(2).BorderTop(1).BorderColor(Colors.Grey.Medium);
                        c.Item().Row(r =>
                        {
                            r.RelativeItem().Text("Grand Total").Bold();
                            r.ConstantItem(110).AlignRight().Text(Money(doc.TotalAmount)).Bold();
                        });
                    });

                    if (!string.IsNullOrWhiteSpace(doc.Remarks))
                        col.Item().PaddingTop(10).Text($"Remarks: {doc.Remarks}").FontSize(8);
                });

                page.Footer().Column(col =>
                {
                    col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                    col.Item().PaddingTop(4).Row(row =>
                    {
                        row.RelativeItem().Text("This is a computer-generated document.").FontSize(7).FontColor(Colors.Grey.Darken1);
                        row.ConstantItem(180).AlignRight().Text($"for {company.Name}").FontSize(8);
                    });
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
