using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class DealerConfiguration : IEntityTypeConfiguration<Dealer>
{
    public void Configure(EntityTypeBuilder<Dealer> b)
    {
        b.ToTable("dealers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");

        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();

        b.Property(x => x.WarrantyMonths).HasColumnName("warranty_months").HasDefaultValue(0);
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(500);
        b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
