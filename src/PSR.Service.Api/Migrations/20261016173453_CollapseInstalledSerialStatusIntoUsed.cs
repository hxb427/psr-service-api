using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class CollapseInstalledSerialStatusIntoUsed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // INSTALLED and USED were one state written two ways: a unit deployed at a customer,
            // owner CUSTOMER, off the technician's holding, counted nowhere on the shelf. Nothing
            // read them apart, so all the pair did was ask whoever opened the SN screen which word
            // meant what. USED is the survivor; the enum no longer has the other, and a row left
            // saying INSTALLED would fail to materialise at all.
            migrationBuilder.Sql("UPDATE component_serials SET status = 'Used' WHERE status = 'Installed';");

            // The audit trail too, and deliberately. It is the same physical event under a name the
            // system no longer has, and leaving it would put the word back on the very screen this
            // is clearing - the detail window reads these rows. A transition that happened still
            // reads as the transition that happened; only the label moves.
            migrationBuilder.Sql("UPDATE serial_status_history SET new_status = 'Used' WHERE new_status = 'Installed';");
            migrationBuilder.Sql("UPDATE serial_status_history SET old_status = 'Used' WHERE old_status = 'Installed';");

            // And the remark the install wrote alongside them. The detail window prints status and
            // remark in adjacent columns, so a row left as "Used | Installed for Acme Ltd" would put
            // the retired word back beside the surviving one on exactly the screen being cleared.
            // Prefix swap only - the customer name, which is the part anyone reads this column for,
            // is carried over untouched. 15 is the first character after "Installed for ".
            migrationBuilder.Sql(
                "UPDATE serial_status_history SET remarks = CONCAT('Deployed to ', SUBSTRING(remarks, 15)) "
                + "WHERE remarks LIKE 'Installed for %';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo. Which units were INSTALLED rather than USED is not recoverable from
            // component_serials - the two were the same row in the same shape - and guessing it back
            // from service line types would invent a split for sale and swap units that never had
            // one. Rolling this migration back leaves the data collapsed, which is the honest
            // outcome; it is said here rather than discovered.
        }
    }
}
