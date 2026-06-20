using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

public class ServiceConfiguration : IEntityTypeConfiguration<ServiceJob>
{
    public void Configure(EntityTypeBuilder<ServiceJob> b)
    {
        b.ToTable("services");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ServiceNo).HasColumnName("service_no").HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.ServiceNo).IsUnique();
        b.Property(x => x.ChallanNo).HasColumnName("challan_no").HasMaxLength(50);
        b.HasIndex(x => x.ChallanNo);
        b.Property(x => x.CustomerType).HasColumnName("customer_type").HasMaxLength(30);

        b.Property(x => x.CustomerId).HasColumnName("customer_id");
        b.Property(x => x.DealerId).HasColumnName("dealer_id");
        b.Property(x => x.SerialNo).HasColumnName("serial_no").HasMaxLength(100);
        b.Property(x => x.PsCode).HasColumnName("ps_code").HasMaxLength(50);
        b.Property(x => x.ModelName).HasColumnName("model_name").HasMaxLength(100);
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
        b.Property(x => x.ReportedProblem).HasColumnName("reported_problem").HasMaxLength(1000);
        b.Property(x => x.WarrantyStatus).HasColumnName("warranty_status").HasConversion<string>().HasMaxLength(20);

        b.Property(x => x.InwardDcNo).HasColumnName("inward_dc_no").HasMaxLength(50);
        b.Property(x => x.OutwardDcNo).HasColumnName("outward_dc_no").HasMaxLength(50);
        b.Property(x => x.DcDate).HasColumnName("dc_date");
        b.Property(x => x.DateReceived).HasColumnName("date_received");

        b.Property(x => x.TechnicianId).HasColumnName("technician_id");
        b.Property(x => x.Priority).HasColumnName("priority").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.AckStatus).HasColumnName("ack_status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ServiceStatus).HasColumnName("service_status").HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.PaymentStatus).HasColumnName("payment_status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.TechnicianRemarks).HasColumnName("technician_remarks").HasMaxLength(1000);
        b.Property(x => x.IsTotalLoss).HasColumnName("is_total_loss").HasDefaultValue(false);
        b.Property(x => x.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        b.HasIndex(x => x.IsDeleted);

        b.Property(x => x.ReplacementSerialNo).HasColumnName("replacement_serial_no").HasMaxLength(100);
        b.Property(x => x.ReplacementPartId).HasColumnName("replacement_part_id");

        b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();

        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Dealer>().WithMany().HasForeignKey(x => x.DealerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Part>().WithMany().HasForeignKey(x => x.ReplacementPartId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne(l => l.Service).HasForeignKey(l => l.ServiceId).OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.ServiceStatus);
        b.HasIndex(x => x.TechnicianId);
        b.HasIndex(x => x.SerialNo);
        b.HasIndex(x => x.CustomerId);
    }
}

public class ServiceLineConfiguration : IEntityTypeConfiguration<ServiceLine>
{
    public void Configure(EntityTypeBuilder<ServiceLine> b)
    {
        b.ToTable("service_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ServiceId).HasColumnName("service_id");
        b.Property(x => x.LineType).HasColumnName("line_type").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.PartId).HasColumnName("part_id");
        b.Property(x => x.ServiceChargeId).HasColumnName("service_charge_id");
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(255);
        b.Property(x => x.Qty).HasColumnName("qty");
        b.Property(x => x.UnitPrice).HasColumnName("unit_price").HasPrecision(12, 2);
        b.Property(x => x.Amount).HasColumnName("amount").HasPrecision(12, 2);
        b.Property(x => x.ReplacementSerialNo).HasColumnName("replacement_serial_no").HasMaxLength(100);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");

        b.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ServiceCharge>().WithMany().HasForeignKey(x => x.ServiceChargeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ServiceId);
    }
}

public class ServiceStatusHistoryConfiguration : IEntityTypeConfiguration<ServiceStatusHistory>
{
    public void Configure(EntityTypeBuilder<ServiceStatusHistory> b)
    {
        b.ToTable("service_status_history");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ServiceId).HasColumnName("service_id");
        b.Property(x => x.FromStatus).HasColumnName("from_status").HasMaxLength(40);
        b.Property(x => x.ToStatus).HasColumnName("to_status").HasMaxLength(40).IsRequired();
        b.Property(x => x.ChangedByUserId).HasColumnName("changed_by_user_id");
        b.Property(x => x.Note).HasColumnName("note").HasMaxLength(500);
        b.Property(x => x.ChangedAt).HasColumnName("changed_at");

        b.HasIndex(x => x.ServiceId);
    }
}
