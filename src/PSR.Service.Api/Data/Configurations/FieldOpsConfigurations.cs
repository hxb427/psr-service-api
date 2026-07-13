using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class StockIssueSerialConfiguration : IEntityTypeConfiguration<StockIssueSerial>
{
    public void Configure(EntityTypeBuilder<StockIssueSerial> b)
    {
        b.ToTable("stock_issue_serials");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.StockMovementId).HasColumnName("stock_movement_id");
        b.Property(x => x.ComponentSerialId).HasColumnName("component_serial_id");
        b.Property(x => x.AckStatus).HasColumnName("ack_status").HasConversion<string>().HasMaxLength(16);

        b.HasOne<StockMovement>().WithMany().HasForeignKey(x => x.StockMovementId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ComponentSerial>().WithMany().HasForeignKey(x => x.ComponentSerialId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.StockMovementId);
        b.HasIndex(x => x.ComponentSerialId);
    }
}

public class StockIssueAckConfiguration : IEntityTypeConfiguration<StockIssueAck>
{
    public void Configure(EntityTypeBuilder<StockIssueAck> b)
    {
        b.ToTable("stock_issue_acks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.StockMovementId).HasColumnName("stock_movement_id");
        b.Property(x => x.QtyReceived).HasColumnName("qty_received");
        b.Property(x => x.QtyDefective).HasColumnName("qty_defective");
        b.Property(x => x.QtyMissing).HasColumnName("qty_missing");
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(500);
        b.Property(x => x.AckedByUserId).HasColumnName("acked_by_user_id");
        b.Property(x => x.AckedAt).HasColumnName("acked_at");

        b.HasOne<StockMovement>().WithMany().HasForeignKey(x => x.StockMovementId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.StockMovementId).IsUnique();
    }
}

public class StockReturnSerialConfiguration : IEntityTypeConfiguration<StockReturnSerial>
{
    public void Configure(EntityTypeBuilder<StockReturnSerial> b)
    {
        b.ToTable("stock_return_serials");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.StockReturnId).HasColumnName("stock_return_id");
        b.Property(x => x.ComponentSerialId).HasColumnName("component_serial_id");
        b.Property(x => x.Defective).HasColumnName("defective");

        b.HasOne<StockReturn>().WithMany().HasForeignKey(x => x.StockReturnId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ComponentSerial>().WithMany().HasForeignKey(x => x.ComponentSerialId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.StockReturnId);
    }
}

public class TechnicianTransferConfiguration : IEntityTypeConfiguration<TechnicianTransfer>
{
    public void Configure(EntityTypeBuilder<TechnicianTransfer> b)
    {
        b.ToTable("technician_transfers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TransferNo).HasColumnName("transfer_no").HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.TransferNo).IsUnique();
        b.Property(x => x.FromTechnicianId).HasColumnName("from_technician_id");
        b.Property(x => x.ToTechnicianId).HasColumnName("to_technician_id");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(500);
        b.Property(x => x.AcknowledgedAt).HasColumnName("acknowledged_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasMany(x => x.Lines).WithOne(x => x.Transfer)
            .HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.FromTechnicianId);
        b.HasIndex(x => x.ToTechnicianId);
        b.HasIndex(x => x.Status);
    }
}

public class TechnicianTransferLineConfiguration : IEntityTypeConfiguration<TechnicianTransferLine>
{
    public void Configure(EntityTypeBuilder<TechnicianTransferLine> b)
    {
        b.ToTable("technician_transfer_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TransferId).HasColumnName("transfer_id");
        b.Property(x => x.PartId).HasColumnName("part_id");
        b.Property(x => x.Qty).HasColumnName("qty");
        b.Property(x => x.QtyReceived).HasColumnName("qty_received");
        b.Property(x => x.QtyDefective).HasColumnName("qty_defective");
        b.Property(x => x.QtyMissing).HasColumnName("qty_missing");

        b.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Serials).WithOne(x => x.Line)
            .HasForeignKey(x => x.TransferLineId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TechnicianTransferSerialConfiguration : IEntityTypeConfiguration<TechnicianTransferSerial>
{
    public void Configure(EntityTypeBuilder<TechnicianTransferSerial> b)
    {
        b.ToTable("technician_transfer_serials");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TransferLineId).HasColumnName("transfer_line_id");
        b.Property(x => x.ComponentSerialId).HasColumnName("component_serial_id");
        b.Property(x => x.AckStatus).HasColumnName("ack_status").HasConversion<string>().HasMaxLength(16);

        b.HasOne<ComponentSerial>().WithMany().HasForeignKey(x => x.ComponentSerialId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class FieldServiceConfiguration : IEntityTypeConfiguration<FieldService>
{
    public void Configure(EntityTypeBuilder<FieldService> b)
    {
        b.ToTable("field_services");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ServiceNo).HasColumnName("service_no").HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.ServiceNo).IsUnique();
        b.Property(x => x.TechnicianId).HasColumnName("technician_id");
        b.Property(x => x.CustomerName).HasColumnName("customer_name").HasMaxLength(200).IsRequired();
        b.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(50);
        b.Property(x => x.Place).HasColumnName("place").HasMaxLength(200);
        b.Property(x => x.MachineSerial).HasColumnName("machine_serial").HasMaxLength(100);
        b.Property(x => x.Complaint).HasColumnName("complaint").HasMaxLength(1000);
        b.Property(x => x.WorkDone).HasColumnName("work_done").HasMaxLength(1000);
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(500);
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");

        b.HasMany(x => x.Lines).WithOne(x => x.FieldService)
            .HasForeignKey(x => x.FieldServiceId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.TechnicianId);
        b.HasIndex(x => x.CreatedAt);
    }
}

public class FieldServiceLineConfiguration : IEntityTypeConfiguration<FieldServiceLine>
{
    public void Configure(EntityTypeBuilder<FieldServiceLine> b)
    {
        b.ToTable("field_service_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.FieldServiceId).HasColumnName("field_service_id");
        b.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.PartId).HasColumnName("part_id");
        b.Property(x => x.Qty).HasColumnName("qty");
        b.Property(x => x.UnitPrice).HasColumnName("unit_price").HasPrecision(12, 2);
        b.Property(x => x.Amount).HasColumnName("amount").HasPrecision(12, 2);
        b.Property(x => x.SerialNo).HasColumnName("serial_no").HasMaxLength(128);
        b.Property(x => x.Defective).HasColumnName("defective");

        b.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class FieldSaleConfiguration : IEntityTypeConfiguration<FieldSale>
{
    public void Configure(EntityTypeBuilder<FieldSale> b)
    {
        b.ToTable("field_sales");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.SaleNo).HasColumnName("sale_no").HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.SaleNo).IsUnique();
        b.Property(x => x.TechnicianId).HasColumnName("technician_id");
        b.Property(x => x.CustomerName).HasColumnName("customer_name").HasMaxLength(200).IsRequired();
        b.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(50);
        b.Property(x => x.Place).HasColumnName("place").HasMaxLength(200);
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(500);
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");

        b.HasMany(x => x.Lines).WithOne(x => x.FieldSale)
            .HasForeignKey(x => x.FieldSaleId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.TechnicianId);
        b.HasIndex(x => x.CreatedAt);
    }
}

public class FieldSaleLineConfiguration : IEntityTypeConfiguration<FieldSaleLine>
{
    public void Configure(EntityTypeBuilder<FieldSaleLine> b)
    {
        b.ToTable("field_sale_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.FieldSaleId).HasColumnName("field_sale_id");
        b.Property(x => x.PartId).HasColumnName("part_id");
        b.Property(x => x.Qty).HasColumnName("qty");
        b.Property(x => x.UnitPrice).HasColumnName("unit_price").HasPrecision(12, 2);
        b.Property(x => x.Amount).HasColumnName("amount").HasPrecision(12, 2);
        b.Property(x => x.SerialNo).HasColumnName("serial_no").HasMaxLength(128);

        b.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
    }
}
