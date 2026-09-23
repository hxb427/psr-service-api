using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSwapReplacementsAndJobKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "job_kind",
                table: "services",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Customer")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "parent_service_job_id",
                table: "services",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "service_replacements",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    original_service_job_id = table.Column<long>(type: "bigint", nullable: false),
                    retained_service_job_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    outgoing_part_id = table.Column<long>(type: "bigint", nullable: true),
                    outgoing_serial_no = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    outgoing_component_serial_id = table.Column<long>(type: "bigint", nullable: true),
                    incoming_part_id = table.Column<long>(type: "bigint", nullable: true),
                    incoming_serial_no = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    incoming_component_serial_id = table.Column<long>(type: "bigint", nullable: true),
                    incoming_serial_created = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    status_before_swap = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    customer_id = table.Column<long>(type: "bigint", nullable: true),
                    dealer_id = table.Column<long>(type: "bigint", nullable: true),
                    reason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    approved_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    cancelled_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    cancelled_by_user_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_replacements", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_services_job_kind",
                table: "services",
                column: "job_kind");

            migrationBuilder.CreateIndex(
                name: "IX_services_parent_service_job_id",
                table: "services",
                column: "parent_service_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_replacements_incoming_serial_no",
                table: "service_replacements",
                column: "incoming_serial_no");

            migrationBuilder.CreateIndex(
                name: "IX_service_replacements_original_service_job_id",
                table: "service_replacements",
                column: "original_service_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_replacements_outgoing_serial_no",
                table: "service_replacements",
                column: "outgoing_serial_no");

            migrationBuilder.CreateIndex(
                name: "IX_service_replacements_retained_service_job_id",
                table: "service_replacements",
                column: "retained_service_job_id");

            // ---------------------------------------------------------------- backfill
            //
            // Every job raised off a faulty field return is a FieldReturn job, and source_component_serial_id
            // is exactly the set - it is set by that flow and by nothing else. Without this they keep
            // counting as customer jobs in the lists and the turnaround figures, which they never were.
            migrationBuilder.Sql(
                "UPDATE services SET job_kind = 'FieldReturn' WHERE source_component_serial_id IS NOT NULL;");

            // Replacements issued before this table existed. They were all total-loss replacements -
            // the only kind there was - and the job row is the whole record of them: the unit that went
            // out, who got it, and (via updated_at) roughly when. Carrying them over is what makes one
            // serial lookup answer "was this one of ours?" for the shop's whole history rather than
            // only for units replaced from today onward.
            //
            // retained_service_job_id stays null: nothing was kept, because the incoming unit was
            // written off. status_before_swap stays null for the same reason - there is no swap to undo.
            migrationBuilder.Sql(@"
                INSERT INTO service_replacements
                    (original_service_job_id, kind, outgoing_part_id, outgoing_serial_no,
                     customer_id, dealer_id, reason, approved_by_user_id, created_at)
                SELECT s.id, 'TotalLoss', s.replacement_part_id, TRIM(s.replacement_serial_no),
                       s.customer_id, s.dealer_id, 'Backfilled from the job record', s.created_by_user_id,
                       COALESCE(s.updated_at, s.created_at)
                FROM services s
                WHERE s.replacement_serial_no IS NOT NULL AND TRIM(s.replacement_serial_no) <> '';");

            // Link each backfilled row to the unit's record where one exists. Matched on
            // (part, serial) because that is component_serials' own uniqueness; a replacement whose
            // part was never resolved, or whose part is not serial-tracked, simply has no unit record
            // and keeps a null - the row is still findable by serial, which is the point of it.
            migrationBuilder.Sql(@"
                UPDATE service_replacements r
                JOIN component_serials c
                  ON c.part_id = r.outgoing_part_id AND c.serial_number = r.outgoing_serial_no
                SET r.outgoing_component_serial_id = c.id
                WHERE r.outgoing_component_serial_id IS NULL AND r.outgoing_part_id IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The backfilled rows go with the table. Nothing else needs undoing: job_kind is dropped
            // below, and dropping a column takes its values with it.
            migrationBuilder.DropTable(
                name: "service_replacements");

            migrationBuilder.DropIndex(
                name: "IX_services_job_kind",
                table: "services");

            migrationBuilder.DropIndex(
                name: "IX_services_parent_service_job_id",
                table: "services");

            migrationBuilder.DropColumn(
                name: "job_kind",
                table: "services");

            migrationBuilder.DropColumn(
                name: "parent_service_job_id",
                table: "services");
        }
    }
}
