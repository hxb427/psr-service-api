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
        b.Property(x => x.OutwardReferenceNo).HasColumnName("outward_reference_no").HasMaxLength(80);
        b.Property(x => x.DcDate).HasColumnName("dc_date");
        b.Property(x => x.DateReceived).HasColumnName("date_received");

        b.Property(x => x.PiNo).HasColumnName("pi_no").HasMaxLength(40);
        b.Property(x => x.PiDate).HasColumnName("pi_date");
        b.Property(x => x.InvNo).HasColumnName("inv_no").HasMaxLength(40);
        b.Property(x => x.InvDate).HasColumnName("inv_date");
        b.HasIndex(x => x.PiNo);

        b.Property(x => x.TechnicianId).HasColumnName("technician_id");
        b.Property(x => x.PromisedDate).HasColumnName("promised_date");
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

        b.Property(x => x.SourceComponentSerialId).HasColumnName("source_component_serial_id");
        b.Property(x => x.JobKind).HasColumnName("job_kind").HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(JobKind.Customer);
        b.Property(x => x.ParentServiceJobId).HasColumnName("parent_service_job_id");
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
        // The lists filter the shop's own units out of the customer sections on this, so it is read
        // on every page of every service query.
        b.HasIndex(x => x.JobKind);
        b.HasIndex(x => x.ParentServiceJobId);
    }
}

public class ServiceReplacementConfiguration : IEntityTypeConfiguration<ServiceReplacement>
{
    public void Configure(EntityTypeBuilder<ServiceReplacement> b)
    {
        b.ToTable("service_replacements");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.OriginalServiceJobId).HasColumnName("original_service_job_id");
        b.Property(x => x.RetainedServiceJobId).HasColumnName("retained_service_job_id");
        b.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(20);

        b.Property(x => x.OutgoingPartId).HasColumnName("outgoing_part_id");
        b.Property(x => x.OutgoingSerialNo).HasColumnName("outgoing_serial_no").HasMaxLength(100);
        b.Property(x => x.OutgoingComponentSerialId).HasColumnName("outgoing_component_serial_id");

        b.Property(x => x.IncomingPartId).HasColumnName("incoming_part_id");
        b.Property(x => x.IncomingSerialNo).HasColumnName("incoming_serial_no").HasMaxLength(100);
        b.Property(x => x.IncomingComponentSerialId).HasColumnName("incoming_component_serial_id");
        b.Property(x => x.IncomingSerialCreated).HasColumnName("incoming_serial_created").HasDefaultValue(false);

        b.Property(x => x.StatusBeforeSwap).HasColumnName("status_before_swap").HasMaxLength(40);
        b.Property(x => x.CustomerId).HasColumnName("customer_id");
        b.Property(x => x.DealerId).HasColumnName("dealer_id");
        b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500);
        b.Property(x => x.ApprovedByUserId).HasColumnName("approved_by_user_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
        b.Property(x => x.CancelledByUserId).HasColumnName("cancelled_by_user_id");

        // Both serials are looked up by hand at the counter, by somebody holding a unit and asking
        // where it came from. Neither is unique: a serial can go out as a replacement, come back, be
        // repaired and go out again.
        b.HasIndex(x => x.OutgoingSerialNo);
        b.HasIndex(x => x.IncomingSerialNo);
        b.HasIndex(x => x.OriginalServiceJobId);
        b.HasIndex(x => x.RetainedServiceJobId);
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
