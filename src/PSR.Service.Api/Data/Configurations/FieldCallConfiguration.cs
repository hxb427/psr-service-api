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
        // Same widths as the equivalents on field_services, so a call's details still fit after a
        // service entry copies them across. A complaint narrowed here would truncate on the way in
        // and leave the register holding half a sentence the visit holds in full.
        b.Property(x => x.ContactPhone).HasColumnName("contact_phone").HasMaxLength(50);
        b.Property(x => x.ReportedComplaint).HasColumnName("reported_complaint").HasMaxLength(1000);
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
        // datetime, not date, unlike the two business days above: this one IS a moment, and the
        // question it answers is "when was this last touched", not "on what day".
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        // The status's own stamp, and the only column the office's turnaround figure can be read
        // from - see FieldCall.CompletedAt for why updated_at cannot stand in for it. datetime like
        // the two above, because it is a moment and not a day.
        b.Property(x => x.CompletedAt).HasColumnName("completed_at");

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
        // What the critical-alert query asks for: the still-open calls, oldest first. Status alone
        // has two values and so is an index MySQL is entitled to ignore; with the created stamp
        // behind it the same read is "the Incomplete ones older than this", which is a range over a
        // prefix. The single-column index above is left in place - the register's other filters ask
        // for status without an age, and the table takes a handful of inserts a day.
        b.HasIndex(x => new { x.CallStatus, x.CreatedAt });
    }
}

/// <summary>Schema for the per-day attendance log. The one thing a call does carry lines of.</summary>
public class FieldCallAttendanceConfiguration : IEntityTypeConfiguration<FieldCallAttendance>
{
    public void Configure(EntityTypeBuilder<FieldCallAttendance> b)
    {
        b.ToTable("field_call_attendances");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");

        b.Property(x => x.FieldCallId).HasColumnName("field_call_id");
        // `date` for the same reason the register's other business days are: this is a day somebody
        // was somewhere, and a column that can hold a time of day invites one to be stored.
        b.Property(x => x.AttendedDate).HasColumnName("attended_date").HasColumnType("date");
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(1000);
        b.Property(x => x.MarkedByUserId).HasColumnName("marked_by_user_id");
        // datetime: a moment, unlike the day above.
        b.Property(x => x.CreatedAt).HasColumnName("created_at");

        // Cascade, unlike the Restrict used everywhere else in this schema, and for a reason that
        // does not apply there: these rows are PART of the call rather than a reference to it. An
        // attendance with no call is not history worth protecting, it is a row nothing can reach.
        // (Nothing deletes a call today; this says what should happen if that ever changes.)
        b.HasOne<FieldCall>().WithMany().HasForeignKey(x => x.FieldCallId).OnDelete(DeleteBehavior.Cascade);
        // Restrict, because this one IS a reference: removing an account must not quietly edit the
        // record of who marked a day.
        b.HasOne<User>().WithMany().HasForeignKey(x => x.MarkedByUserId).OnDelete(DeleteBehavior.Restrict);

        // One attendance per call per day, enforced in the schema rather than only in the handler.
        // This is what keeps a retry on a dropped field connection from becoming a second visit —
        // the handler checks first, but two taps racing each other would both pass that check.
        b.HasIndex(x => new { x.FieldCallId, x.AttendedDate }).IsUnique();
    }
}
