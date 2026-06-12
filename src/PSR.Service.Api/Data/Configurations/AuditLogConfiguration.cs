using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("audit_log");

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");

        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Action).HasColumnName("action").HasMaxLength(100).IsRequired();
        b.Property(x => x.Entity).HasColumnName("entity").HasMaxLength(100);
        b.Property(x => x.EntityId).HasColumnName("entity_id");
        b.Property(x => x.Details).HasColumnName("details").HasMaxLength(4000);
        b.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(50);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");

        b.HasIndex(x => x.UserId);
        b.HasIndex(x => x.CreatedAt);
        b.HasIndex(x => new { x.Entity, x.EntityId });
    }
}
