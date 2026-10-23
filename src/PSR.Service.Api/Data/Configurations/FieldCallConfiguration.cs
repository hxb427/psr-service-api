using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Configurations;

/// <summary>Schema for the engineer call register. In its own file rather than appended to
/// <see cref="FieldCallConfiguration"/>'s neighbours in FieldOpsConfigurations: a call carries no
/// lines, no money and no serial custody, so it shares nothing with the field service and sale
/// configurations beyond the word "field".</summary>
public class FieldCallConfiguration : IEntityTypeConfiguration<FieldCall>
{
    public void Configure(EntityTypeBuilder<FieldCall> b)
    {
        b.ToTable("field_calls");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");

        b.Property(x => x.CallNo).HasColumnName("call_no").HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.CallNo).IsUnique();

        // Stored as the enum name, like every other enum in this schema: a register read straight out
        // of MySQL by someone chasing a number should say "DealerVisit", not "4".
        b.Property(x => x.CallType).HasColumnName("call_type").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.EngineerId).HasColumnName("engineer_id");
        b.Property(x => x.CallReceivedDate).HasColumnName("call_received_date").HasColumnType("date");
        b.Property(x => x.Locality).HasColumnName("locality").HasConversion<string>().HasMaxLength(16);

        b.Property(x => x.CustomerName).HasColumnName("customer_name").HasMaxLength(200).IsRequired();
        b.Property(x => x.CustomerId).HasColumnName("customer_id");
        b.Property(x => x.CustomerAddress).HasColumnName("customer_address").HasMaxLength(500);

        b.Property(x => x.CallReferenceNo).HasColumnName("call_reference_no").HasMaxLength(100);
        b.Property(x => x.MachineModel).HasColumnName("machine_model").HasMaxLength(100);
        b.Property(x => x.MachineSerial).HasColumnName("machine_serial").HasMaxLength(100);
        b.Property(x => x.SoftwareVersion).HasColumnName("software_version").HasMaxLength(50);

        // Both business dates are `date`, not datetime: they are days, and a column that can hold a
        // time of day invites one to be stored, after which "calls received on the 14th" depends on
        // what time zone the reader assumes. See ShopClock.
        b.Property(x => x.AttendedDate).HasColumnName("attended_date").HasColumnType("date");
        b.Property(x => x.CallStatus).HasColumnName("call_status").HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(1000);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");

        // Restrict both ways, as everywhere else in this schema: a register entry naming a customer
        // must not be deletable out from under the register, and deleting an engineer's account must
        // not take their call history with it.
        b.HasOne<User>().WithMany().HasForeignKey(x => x.EngineerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.EngineerId);
        // The register's own sort order, and what the list endpoint pages on. Received date rather
        // than created_at: the office asks "which calls came in last week", and a call keyed in three
        // days late belongs to the day it arrived, not the day someone got round to typing it.
        b.HasIndex(x => x.CallReceivedDate);
        b.HasIndex(x => x.CallType);
        b.HasIndex(x => x.CallStatus);
    }
}
