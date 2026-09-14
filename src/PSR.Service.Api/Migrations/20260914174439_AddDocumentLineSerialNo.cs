using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentLineSerialNo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "serial_no",
                table: "service_document_lines",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            // Backfill from the job each line already points at, so reprinting a document raised before
            // this column existed shows the serial instead of a dash. The snapshot is only frozen from
            // here on; for the old rows the job is the one record that still knows what the serial was.
            // Spare-sale lines carry no job and stay null — catalogue goods have no unit serial.
            migrationBuilder.Sql(@"
UPDATE `service_document_lines` l
JOIN `services` s ON s.`id` = l.`service_job_id`
SET l.`serial_no` = s.`serial_no`
WHERE l.`service_job_id` IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "serial_no",
                table: "service_document_lines");
        }
    }
}
