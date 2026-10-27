using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Documents;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The parts delivery challan — spares going out to a dealer or a field technician as paperwork
/// only, raised from the stock register.
///
/// Two things about it are easy to break from a long way away and neither fails the build. The first is
/// its number book: it has its own (PDC), because the customer challan register is read as one continuous
/// run and every number out of the store's issue-docket book is a consignment that physically left, so
/// taking a number from either would put one in it with nothing behind it. The second is the table: its
/// lines are catalogue items, so it has to print the spare-sale columns rather than three columns of
/// dashes where a serviced unit's serial, warranty and inward challan would go.
///
/// What it must NOT do — move stock — is structural rather than tested here: <see cref="BillingService"/>
/// is constructed without any stock service at all (asserted below), and the generate path adds the
/// document and nothing else.</summary>
public class PartsChallanTests
{
    static PartsChallanTests() => QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

    /// <summary>A challan as the generate path builds one: a dealer, goods lines off the catalogue, and
    /// neither a service job nor a sale behind it.</summary>
    private static ServiceDocument PartsChallan()
    {
        var doc = new ServiceDocument
        {
            DocType = DocumentType.DC,
            DocNo = "PDC-2026-0007",
            DocDate = new DateTime(2026, 10, 27),
            PartyName = "SREE VENKATESHWARA AGENCIES",
            PartyAddress = "12/440, Bypass Junction, Vytilla, Ernakulam - 682019",
            PartyGstin = "32AABCS1429B1Z1",
            PartyState = "Kerala",
            PartyStateCode = "32",
            ConsigneeAddress = "Warehouse 4, Container Road, Vytilla, Ernakulam - 682019",
            CourierMode = "Professional Couriers",
            CourierCharges = 180m,
            CourierHsnCode = ServiceDocument.CourierSacCode,
            TaxableAmount = 3400m,
            CgstAmount = 306m,
            SgstAmount = 306m,
            TotalAmount = 4192m,
        };
        doc.Lines.Add(new ServiceDocumentLine
        {
            ServiceJobId = null,
            PartId = 41,
            Description = "Load cell 500kg stainless, single point (PS-LC-500S)",
            HsnCode = "84239090",
            Unit = "Nos",
            Qty = 2, UnitRate = 1200m, TaxableAmount = 2400m, GstPercent = 18m, TaxAmount = 432m,
            LineTotal = 2832m,
        });
        doc.Lines.Add(new ServiceDocumentLine
        {
            ServiceJobId = null,
            PartId = 58,
            Description = "Indicator keypad membrane (PS-IND-KPD)",
            HsnCode = "84733099",
            Unit = "Pcs",
            Qty = 4, UnitRate = 250m, TaxableAmount = 1000m, GstPercent = 18m, TaxAmount = 180m,
            LineTotal = 1180m,
        });
        return doc;
    }

    // ---------------------------------------------------------------- its own number book

    /// <summary>The three challan books stay three. Collapsing any two of them is the "simplification"
    /// that looks harmless in the constants file and shows up as a gap in an audited register.</summary>
    [Fact]
    public void The_parts_challan_draws_from_its_own_book()
    {
        SequenceKeys.PartsChallan.Should().NotBe(SequenceKeys.DeliveryChallan,
            "the DC book is the customer's outward challan for a repaired unit, audited as one run");
        SequenceKeys.PartsChallan.Should().NotBe(SequenceKeys.StockIssueDc,
            "every SDC number is a consignment that left the store; this document moves nothing");
    }

    /// <summary>Year-scoped like the other document books, so a challan reads PDC-2026-0001 and the
    /// counter resets with the calendar. A row with a null year would number it PDC00001 instead.</summary>
    [Fact]
    public void The_parts_challan_book_is_year_scoped_under_its_own_prefix()
    {
        var row = SeededSequences().Single(r => (string)r[nameof(NumberSequence.Key)]! == SequenceKeys.PartsChallan);

        row[nameof(NumberSequence.Prefix)].Should().Be("PDC");
        row[nameof(NumberSequence.Year)].Should().NotBeNull("PREFIX-YYYY-NNNN, like PI, INVOICE and DC");
    }

    // ---------------------------------------------------------------- the table it prints

    /// <summary>Goods lines, so the spare-sale column set. Asserted against a service document and a sale
    /// document in the same test: the guard reads the lines, and the point is that it answers the same way
    /// for a challan that bills no sale as it does for the sale itself.</summary>
    [Fact]
    public void A_parts_challan_prints_the_goods_columns()
    {
        DocumentPdf.ShowsGoodsColumns(PartsChallan()).Should().BeTrue();

        var serviceDoc = new ServiceDocument { DocType = DocumentType.DC };
        serviceDoc.Lines.Add(new ServiceDocumentLine { ServiceJobId = 1, Description = "Unit repair" });
        DocumentPdf.ShowsGoodsColumns(serviceDoc).Should().BeFalse(
            "a serviced unit's serial, warranty and inward challan are what those columns are for");

        var saleDoc = new ServiceDocument { DocType = DocumentType.Invoice, SpareSaleId = 5 };
        saleDoc.Lines.Add(new ServiceDocumentLine { PartId = 9, Description = "Cable gland" });
        DocumentPdf.ShowsGoodsColumns(saleDoc).Should().BeTrue("unchanged — this is where the layout came from");
    }

    /// <summary>QuestPDF throws rather than clipping when a table will not fit the page, so rendering a
    /// realistic challan is a genuine width check on the one document nobody looks at until it is being
    /// handed to a courier.</summary>
    [Fact]
    public void A_parts_challan_fits_the_page()
    {
        var act = () => DocumentPdf.Render(PartsChallan(), new CompanyInfo());

        act.Should().NotThrow("a table wider than A4 fails the download, not the build");
        act().Should().NotBeEmpty();
    }

    /// <summary>The watermarked path, which every generation goes through before anything is saved.</summary>
    [Fact]
    public void A_parts_challan_preview_fits_the_page()
    {
        var act = () => DocumentPdf.Render(PartsChallan(), new CompanyInfo(), "PREVIEW — NOT SAVED");

        act.Should().NotThrow();
        act().Should().NotBeEmpty();
    }

    /// <summary>A challan sent out with nothing declared prints as a plain packing list rather than a page
    /// of zeros under a Total Amount of Rs. 0.00 — the same guard the service challan has always used, and
    /// what a part with no rate on its catalogue row produces.</summary>
    [Fact]
    public void An_unvalued_parts_challan_prints_without_money()
    {
        var doc = PartsChallan();
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

    /// <summary>Rate × Qty has to equal the Taxable column printed beside it. A parts line is priced
    /// tax-EXCLUSIVE — the parts master's own convention, and the spare sale's — so the stored rate
    /// prints as it stands rather than being derived back out of the taxable amount.</summary>
    [Fact]
    public void A_parts_line_prints_the_rate_it_was_priced_at()
    {
        foreach (var l in PartsChallan().Lines)
            DocumentPdf.LineRate(l).Should().Be(l.UnitRate);
    }

    // ---------------------------------------------------------------- and moves nothing

    /// <summary>No document path can move stock, because the service that builds them is handed nothing
    /// that moves stock. Pinned here because the cheap way to make a challan "also issue the parts" is to
    /// inject the ledger into this constructor, and that is the one change this feature must not grow.</summary>
    [Fact]
    public void Nothing_that_builds_a_document_can_move_stock()
    {
        var parameters = typeof(BillingService).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType.Name).ToList();

        parameters.Should().NotContain("StockLedgerService");
        parameters.Should().NotContain("SerialService");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Seed data off the design-time model — it is stripped from the runtime one. Same approach
    /// as NumberSequenceSeedTests; no database is touched.</summary>
    private static IReadOnlyList<IDictionary<string, object?>> SeededSequences()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql("Server=localhost;Database=psr_service_model_only;User=none;Password=none",
                new MySqlServerVersion(new Version(8, 0, 32)))
            .Options;
        using var db = new AppDbContext(options);
        var model = db.GetService<IDesignTimeModel>().Model;
        return model.FindEntityType(typeof(NumberSequence))!.GetSeedData().ToList();
    }
}
