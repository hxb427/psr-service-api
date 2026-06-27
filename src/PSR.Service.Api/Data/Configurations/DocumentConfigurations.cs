using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class ServiceDocumentConfiguration : IEntityTypeConfiguration<ServiceDocument>
{
    public void Configure(EntityTypeBuilder<ServiceDocument> b)
    {
        b.ToTable("service_documents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.DocType).HasColumnName("doc_type").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.DocNo).HasColumnName("doc_no").HasMaxLength(40).IsRequired();
        b.Property(x => x.DocDate).HasColumnName("doc_date");
        b.Property(x => x.ServiceId).HasColumnName("service_id");
        b.Property(x => x.SpareSaleId).HasColumnName("spare_sale_id");

        b.Property(x => x.PartyName).HasColumnName("party_name").HasMaxLength(200).IsRequired();
        b.Property(x => x.PartyAddress).HasColumnName("party_address").HasMaxLength(500);
        b.Property(x => x.PartyGstin).HasColumnName("party_gstin").HasMaxLength(20);
        b.Property(x => x.PartyState).HasColumnName("party_state").HasMaxLength(60);
        b.Property(x => x.PartyStateCode).HasColumnName("party_state_code").HasMaxLength(10);
        b.Property(x => x.IsInterState).HasColumnName("is_inter_state");

        b.Property(x => x.TaxableAmount).HasColumnName("taxable_amount").HasPrecision(14, 2);
        b.Property(x => x.CgstAmount).HasColumnName("cgst_amount").HasPrecision(14, 2);
        b.Property(x => x.SgstAmount).HasColumnName("sgst_amount").HasPrecision(14, 2);
        b.Property(x => x.IgstAmount).HasColumnName("igst_amount").HasPrecision(14, 2);
        b.Property(x => x.CourierCharges).HasColumnName("courier_charges").HasPrecision(14, 2);
        b.Property(x => x.TotalAmount).HasColumnName("total_amount").HasPrecision(14, 2);
        b.Property(x => x.CourierMode).HasColumnName("courier_mode").HasMaxLength(80);
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(500);

        b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");

        // Unique per type (a PI number and an INVOICE number never collide because the type differs).
        b.HasIndex(x => new { x.DocType, x.DocNo }).IsUnique();
        b.HasIndex(x => x.ServiceId);

        b.HasOne<ServiceJob>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne(l => l.Document).HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ServiceDocumentLineConfiguration : IEntityTypeConfiguration<ServiceDocumentLine>
{
    public void Configure(EntityTypeBuilder<ServiceDocumentLine> b)
    {
        b.ToTable("service_document_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.DocumentId).HasColumnName("document_id");
        b.Property(x => x.ServiceJobId).HasColumnName("service_job_id");
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(300).IsRequired();
        b.Property(x => x.Warranty).HasColumnName("warranty").HasMaxLength(30);
        b.Property(x => x.ServiceChallan).HasColumnName("service_challan").HasMaxLength(50);
        b.Property(x => x.HsnCode).HasColumnName("hsn_code").HasMaxLength(20);
        b.Property(x => x.Qty).HasColumnName("qty");
        b.Property(x => x.UnitRate).HasColumnName("unit_rate").HasPrecision(14, 2);
        b.Property(x => x.TaxableAmount).HasColumnName("taxable_amount").HasPrecision(14, 2);
        b.Property(x => x.GstPercent).HasColumnName("gst_percent").HasPrecision(5, 2);
        b.Property(x => x.TaxAmount).HasColumnName("tax_amount").HasPrecision(14, 2);
        b.Property(x => x.LineTotal).HasColumnName("line_total").HasPrecision(14, 2);
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(300);

        b.HasIndex(x => x.DocumentId);
        b.HasIndex(x => x.ServiceJobId);
    }
}
