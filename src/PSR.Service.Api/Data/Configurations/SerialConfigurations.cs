using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class ComponentSerialConfiguration : IEntityTypeConfiguration<ComponentSerial>
{
    public void Configure(EntityTypeBuilder<ComponentSerial> b)
    {
        b.ToTable("component_serials");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.PartId).HasColumnName("part_id");
        b.Property(x => x.SerialNumber).HasColumnName("serial_number").HasMaxLength(128).IsRequired();
        b.Property(x => x.ItemName).HasColumnName("item_name").HasMaxLength(255);
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.OwnerType).HasColumnName("owner_type").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.OwnerRef).HasColumnName("owner_ref").HasMaxLength(200);
        b.Property(x => x.TechnicianId).HasColumnName("technician_id");
        b.Property(x => x.LastUpdatedAt).HasColumnName("last_updated_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");

        b.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PartId, x.SerialNumber }).IsUnique();
        b.HasIndex(x => x.Status);
        b.HasIndex(x => new { x.OwnerType, x.TechnicianId });
    }
}

public class SerialStatusHistoryConfiguration : IEntityTypeConfiguration<SerialStatusHistory>
{
    public void Configure(EntityTypeBuilder<SerialStatusHistory> b)
    {
        b.ToTable("serial_status_history");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ComponentSerialId).HasColumnName("component_serial_id");
        b.Property(x => x.PartId).HasColumnName("part_id");
        b.Property(x => x.SerialNumber).HasColumnName("serial_number").HasMaxLength(128).IsRequired();
        b.Property(x => x.OldStatus).HasColumnName("old_status").HasMaxLength(20);
        b.Property(x => x.NewStatus).HasColumnName("new_status").HasMaxLength(20).IsRequired();
        b.Property(x => x.ChangedByUserId).HasColumnName("changed_by_user_id");
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(500);
        b.Property(x => x.ChangedAt).HasColumnName("changed_at");

        b.HasIndex(x => x.ComponentSerialId);
        b.HasIndex(x => x.SerialNumber);
        b.HasIndex(x => x.ChangedAt);
    }
}
