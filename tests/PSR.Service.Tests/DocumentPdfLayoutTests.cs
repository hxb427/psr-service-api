using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Documents;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>QuestPDF throws rather than clipping when a table cannot fit the page width, so rendering a
/// realistic document is a genuine layout check — which is worth having now that the service table
/// carries a serial column and the columns around it were re-cut to make room. A9 is not the concern;
/// the concern is someone widening a column later and finding out from a failed invoice download.</summary>
public class DocumentPdfLayoutTests
{
    static DocumentPdfLayoutTests() => QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

    private static ServiceDocument ServiceInvoice(bool withRemarks)
    {
        var doc = new ServiceDocument
        {
            DocType = DocumentType.Invoice,
            DocNo = "INV-2026-0042",
            DocDate = new DateTime(2026, 9, 14),
            PartyName = "SREE VENKATESHWARA AGENCIES",
            PartyAddress = "12/440, Bypass Junction, Vytilla, Ernakulam - 682019",
            PartyGstin = "32AABCS1429B1Z1",
            PartyState = "Kerala",
            PartyStateCode = "32",
            ConsigneeAddress = "Warehouse 4, Container Road, Vytilla, Ernakulam - 682019",
            CourierMode = "Professional Couriers",
            CourierCharges = 250m,
            TaxableAmount = 6000m,
            CgstAmount = 540m,
            SgstAmount = 540m,
            TotalAmount = 7330m,
        };
        doc.Lines.Add(new ServiceDocumentLine
        {
            ServiceJobId = 1,
            Description = "Weighing indicator repair - display module replaced",
            SerialNo = "H26020000006",
            Warranty = nameof(WarrantyStatus.OutOfWarranty),
            ServiceChallan = "CH-2026-0771",
            HsnCode = "84239090",
            Unit = "Nos",
            Qty = 1, UnitRate = 4720m, TaxableAmount = 4000m, GstPercent = 18m, TaxAmount = 720m,
            LineTotal = 4720m,
            Remarks = withRemarks ? "Board no. 44" : null,
        });
        doc.Lines.Add(new ServiceDocumentLine
        {
            ServiceJobId = 2,
            Description = "Platform load cell alignment and calibration",
            // 15 characters — the longest serial the column is cut to hold on one line.
            SerialNo = "TSC-TE244-77120",
            Warranty = nameof(WarrantyStatus.InWarranty),
            ServiceChallan = "CH-2026-0772",
            HsnCode = "998719",
            Unit = "Set",
            Qty = 1, UnitRate = 0m, TaxableAmount = 0m, GstPercent = 18m, TaxAmount = 0m, LineTotal = 0m,
        });
        return doc;
    }

    private static ServiceDocument SaleInvoice()
    {
        var doc = new ServiceDocument
        {
            DocType = DocumentType.Invoice,
            DocNo = "INV-2026-0043",
            DocDate = new DateTime(2026, 9, 14),
            SpareSaleId = 5,
            PartyName = "KOCHI TRADE LINKS",
            PartyAddress = "Door 44/113, Market Road, Palarivattom, Ernakulam - 682025",
            PartyGstin = "33AAFCK9911L1ZP",
            PartyState = "Tamil Nadu",
            PartyStateCode = "33",
            IsInterState = true,
            TaxableAmount = 1050.30m,
            IgstAmount = 187.24m,
            TotalAmount = 1237.54m,
        };
        doc.Lines.Add(new ServiceDocumentLine
        {
            PartId = 9, Description = "Load cell cable gland 12mm (LC-GLD-12)", HsnCode = "84239090",
            Unit = "Pcs",
            Qty = 3, UnitRate = 10.10m, TaxableAmount = 30.30m, GstPercent = 12m, TaxAmount = 3.64m,
            LineTotal = 33.94m,
        });
        return doc;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Service_document_fits_the_page(bool withRemarks)
    {
        var act = () => DocumentPdf.Render(ServiceInvoice(withRemarks), new CompanyInfo(), sourcePiNo: "PI-2026-0119");

        act.Should().NotThrow("a table wider than A4 fails the download, not the build");
        act().Should().NotBeEmpty();
    }

    /// <summary>A priced delivery challan is the widest service table there is: every column the invoice
    /// carries, including the money ones, on a type that used to print without them.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Delivery_challan_fits_the_page(bool withRemarks)
    {
        var doc = ServiceInvoice(withRemarks);
        doc.DocType = DocumentType.DC;
        doc.DocNo = "DC-2026-0088";

        var act = () => DocumentPdf.Render(doc, new CompanyInfo());

        act.Should().NotThrow();
        act().Should().NotBeEmpty();
    }

    /// <summary>A challan raised before a DC carried any value: no declared figures, so nothing to total.
    /// It has to reprint the way it was issued rather than under a Total Amount of Rs. 0.00.</summary>
    [Fact]
    public void An_unvalued_delivery_challan_prints_without_money()
    {
        var doc = ServiceInvoice(withRemarks: true);
        doc.DocType = DocumentType.DC;
        doc.DocNo = "DC-2026-0001";
        foreach (var l in doc.Lines)
        {
            l.UnitRate = 0m;
            l.TaxableAmount = 0m;
            l.TaxAmount = 0m;
            l.LineTotal = 0m;
        }
        doc.TaxableAmount = doc.CgstAmount = doc.SgstAmount = doc.IgstAmount = 0m;
        doc.CourierCharges = 0m;
        doc.TotalAmount = 0m;

        DocumentPdf.ShowsMoney(doc).Should().BeFalse();

        var act = () => DocumentPdf.Render(doc, new CompanyInfo());
        act.Should().NotThrow();
    }

    /// <summary>A challan that declares a value prints the same money columns the invoice does — that is
    /// the whole point of putting prices on it.</summary>
    [Fact]
    public void A_valued_delivery_challan_prints_its_money()
        => DocumentPdf.ShowsMoney(new ServiceDocument { DocType = DocumentType.DC, TotalAmount = 7330m })
            .Should().BeTrue();

    /// <summary>The courier charge is on the document, not on a line, so the only place its SAC can print
    /// is the label of its own totals row.</summary>
    [Fact]
    public void The_courier_row_is_labelled_with_its_sac()
        => DocumentPdf.CourierLabel(new ServiceDocument { CourierHsnCode = ServiceDocument.CourierSacCode })
            .Should().Be("Courier (HSN 9985)");

    /// <summary>A document raised before the code was stamped never carried one, and must not be reprinted
    /// claiming it did.</summary>
    [Fact]
    public void A_courier_row_with_no_stamped_sac_prints_bare()
        => DocumentPdf.CourierLabel(new ServiceDocument { CourierCharges = 250m }).Should().Be("Courier");

    [Fact]
    public void Sale_document_fits_the_page()
    {
        var act = () => DocumentPdf.Render(SaleInvoice(), new CompanyInfo(), sourcePiNo: "PI-2026-0120");

        act.Should().NotThrow();
        act().Should().NotBeEmpty();
    }

    /// <summary>The preview is the same layout with a diagonal wash over it; a foreground element that
    /// upset the page would only show up on the path every generation goes through first.</summary>
    [Fact]
    public void Watermarked_preview_fits_the_page()
    {
        var act = () => DocumentPdf.Render(ServiceInvoice(withRemarks: true), new CompanyInfo(), "PREVIEW — NOT SAVED");

        act.Should().NotThrow();
    }

    /// <summary>A Kerala document prints two tax rows where an inter-state one prints one, which is a row
    /// more in a totals block that has to stay on the page with the signature under it.</summary>
    [Fact]
    public void Intra_state_document_fits_the_page_with_both_tax_heads()
    {
        var doc = ServiceInvoice(withRemarks: true);
        doc.PartyState = "Kerala";
        doc.PartyStateCode = "32";
        doc.IsInterState = false;
        doc.CgstAmount = Math.Round(doc.IgstAmount / 2m, 2);
        doc.SgstAmount = doc.IgstAmount - doc.CgstAmount;
        doc.IgstAmount = 0m;

        var act = () => DocumentPdf.Render(doc, new CompanyInfo(), sourcePiNo: "PI-2026-0119");

        act.Should().NotThrow();
        act().Should().NotBeEmpty();
    }
}
