using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class ServiceChargeConfiguration : IEntityTypeConfiguration<ServiceCharge>
{
    public void Configure(EntityTypeBuilder<ServiceCharge> b)
    {
        b.ToTable("service_charges");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");

        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.Name);

        b.Property(x => x.Charge).HasColumnName("charge").HasPrecision(12, 2);
        b.Property(x => x.TaxPercent).HasColumnName("tax_percent").HasPrecision(5, 2);
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(500);
        b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
