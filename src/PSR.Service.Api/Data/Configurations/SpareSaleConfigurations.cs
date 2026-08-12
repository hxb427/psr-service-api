using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class SpareSaleConfiguration : IEntityTypeConfiguration<SpareSale>
{
    public void Configure(EntityTypeBuilder<SpareSale> b)
    {
        b.ToTable("spare_sales");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.SaleNo).HasColumnName("sale_no").HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.SaleNo).IsUnique();
        b.Property(x => x.SaleDate).HasColumnName("sale_date");

        b.Property(x => x.CustomerType).HasColumnName("customer_type").HasMaxLength(20).IsRequired();
        b.Property(x => x.DealerId).HasColumnName("dealer_id");
        b.Property(x => x.CustomerId).HasColumnName("customer_id");

        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.PaymentStatus).HasColumnName("payment_status").HasConversion<string>().HasMaxLength(20);

        b.Property(x => x.PiNo).HasColumnName("pi_no").HasMaxLength(40);
        b.Property(x => x.PiDate).HasColumnName("pi_date");
        b.Property(x => x.InvNo).HasColumnName("inv_no").HasMaxLength(40);
        b.Property(x => x.InvDate).HasColumnName("inv_date");

        b.Property(x => x.TaxableAmount).HasColumnName("taxable_amount").HasPrecision(14, 2);
        b.Property(x => x.TaxAmount).HasColumnName("tax_amount").HasPrecision(14, 2);
        b.Property(x => x.TotalAmount).HasColumnName("total_amount").HasPrecision(14, 2);

        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(500);
        b.Property(x => x.IsDeleted).HasColumnName("is_deleted");
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasOne<Dealer>().WithMany().HasForeignKey(x => x.DealerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne(l => l.Sale).HasForeignKey(l => l.SpareSaleId).OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.SaleDate);
    }
}

public class SpareSaleLineConfiguration : IEntityTypeConfiguration<SpareSaleLine>
{
    public void Configure(EntityTypeBuilder<SpareSaleLine> b)
    {
        b.ToTable("spare_sale_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.SpareSaleId).HasColumnName("spare_sale_id");
        b.Property(x => x.PartId).HasColumnName("part_id");

        b.Property(x => x.ItemCode).HasColumnName("item_code").HasMaxLength(50).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(300).IsRequired();
        b.Property(x => x.HsnCode).HasColumnName("hsn_code").HasMaxLength(20);
        b.Property(x => x.Unit).HasColumnName("unit").HasMaxLength(20);

        b.Property(x => x.Qty).HasColumnName("qty");
        b.Property(x => x.RateType).HasColumnName("rate_type").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.UnitRate).HasColumnName("unit_rate").HasPrecision(14, 2);
        b.Property(x => x.GstPercent).HasColumnName("gst_percent").HasPrecision(5, 2);
        b.Property(x => x.TaxableAmount).HasColumnName("taxable_amount").HasPrecision(14, 2);
        b.Property(x => x.TaxAmount).HasColumnName("tax_amount").HasPrecision(14, 2);
        b.Property(x => x.LineTotal).HasColumnName("line_total").HasPrecision(14, 2);

        b.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.SpareSaleId);
        b.HasIndex(x => x.PartId);
    }
}

public class SpareSaleReturnConfiguration : IEntityTypeConfiguration<SpareSaleReturn>
{
    public void Configure(EntityTypeBuilder<SpareSaleReturn> b)
    {
        b.ToTable("spare_sale_returns");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.SpareSaleId).HasColumnName("spare_sale_id");
        b.Property(x => x.ReturnNo).HasColumnName("return_no").HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.ReturnNo).IsUnique();
        b.Property(x => x.ReturnDate).HasColumnName("return_date");
        b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        // Restrict, not Cascade: an invoiced sale cannot be deleted anyway, and a return that
        // disappeared with its sale would leave the stock it put back unexplained.
        b.HasOne<SpareSale>().WithMany().HasForeignKey(x => x.SpareSaleId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne(l => l.Return).HasForeignKey(l => l.SpareSaleReturnId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.SpareSaleId);
    }
}

public class SpareSaleReturnLineConfiguration : IEntityTypeConfiguration<SpareSaleReturnLine>
{
    public void Configure(EntityTypeBuilder<SpareSaleReturnLine> b)
    {
        b.ToTable("spare_sale_return_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.SpareSaleReturnId).HasColumnName("spare_sale_return_id");
        b.Property(x => x.PartId).HasColumnName("part_id");
        b.Property(x => x.ItemCode).HasColumnName("item_code").HasMaxLength(50).IsRequired();
        b.Property(x => x.Qty).HasColumnName("qty");

        b.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.SpareSaleReturnId);
        b.HasIndex(x => x.PartId);
    }
}
