using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class AppVersionConfiguration : IEntityTypeConfiguration<AppVersion>
{
    public void Configure(EntityTypeBuilder<AppVersion> b)
    {
        b.ToTable("app_versions");

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");

        b.Property(x => x.ClientId).HasColumnName("client_id").HasMaxLength(40).IsRequired();
        b.Property(x => x.Version).HasColumnName("version").HasMaxLength(20).IsRequired();
        b.Property(x => x.VersionCode).HasColumnName("version_code").IsRequired();
        b.Property(x => x.ApkS3Key).HasColumnName("apk_s3_key").HasMaxLength(500).IsRequired();
        b.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(4000);
        b.Property(x => x.Mandatory).HasColumnName("mandatory").HasDefaultValue(false);
        b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        b.Property(x => x.PublishedAt).HasColumnName("published_at");

        // The updater's only query: newest active build for one client.
        b.HasIndex(x => new { x.ClientId, x.IsActive, x.VersionCode });

        // A build number identifies a build within its own app. Android refuses to install an APK
        // whose versionCode does not exceed the installed one, so letting two rows share a code for
        // one client would publish an update that silently fails on every device.
        b.HasIndex(x => new { x.ClientId, x.VersionCode }).IsUnique();
    }
}
