using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class PartConfiguration : IEntityTypeConfiguration<Part>
{
    public void Configure(EntityTypeBuilder<Part> b)
    {
        b.ToTable("parts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");

        b.Property(x => x.ItemCode).HasColumnName("item_code").HasMaxLength(50).IsRequired();
        b.HasIndex(x => x.ItemCode).IsUnique();

        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        b.Property(x => x.Category).HasColumnName("category").HasMaxLength(100);
        b.Property(x => x.Unit).HasColumnName("unit").HasMaxLength(20);

        b.Property(x => x.PurchaseRate).HasColumnName("purchase_rate").HasPrecision(12, 2);
        b.Property(x => x.DealerRate).HasColumnName("dealer_rate").HasPrecision(12, 2);
        b.Property(x => x.CustomerRate).HasColumnName("customer_rate").HasPrecision(12, 2);
        b.Property(x => x.HsnCode).HasColumnName("hsn_code").HasMaxLength(20);
        b.Property(x => x.GstPercent).HasColumnName("gst_percent").HasPrecision(5, 2);

        b.Property(x => x.IsSerialTracked).HasColumnName("is_serial_tracked").HasDefaultValue(false);
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(500);
        b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasIndex(x => x.Name);
        b.HasIndex(x => x.Category);
    }
}
