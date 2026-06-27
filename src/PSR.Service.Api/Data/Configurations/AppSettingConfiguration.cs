using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> b)
    {
        b.ToTable("app_settings");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasColumnName("key").HasMaxLength(60);
        b.Property(x => x.Value).HasColumnName("value").HasMaxLength(255).IsRequired();

        // Invoice generation is allowed by default; an admin can switch it off.
        b.HasData(new AppSetting { Key = SettingKeys.InvoiceGenerationEnabled, Value = "true" });
    }
}
