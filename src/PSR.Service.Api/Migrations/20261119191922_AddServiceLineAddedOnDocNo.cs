using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceLineAddedOnDocNo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "added_on_doc_no",
                table: "service_lines",
                type: "varchar(40)",
                maxLength: 40,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            // Part lines on a dealer's job were priced at the customer rate — the technician's components
            // and the replacement unit alike — where the old app billed a dealer the dealer rate. New lines
            // now follow PartyRate; this brings the ones already waiting to be billed into line with them,
            // at the same rate with the same fallback (no dealer rate set → customer rate).
            //
            // Only jobs nothing has billed yet: no PI or invoice number, and not on any document at all.
            // Anything a document has priced keeps the figures it was priced at — the invoice after a PI
            // has to match the PI, and a revert is refused under any document for the same reason. Service
            // charges carry one rate for everybody and are untouched. One-way: Down does not restore the
            // customer-rate figures, which were the error.
            migrationBuilder.Sql(@"
UPDATE `service_lines` l
JOIN `services` s ON s.`id` = l.`service_id`
JOIN `parts` p ON p.`id` = l.`part_id`
SET l.`unit_price` = IF(p.`dealer_rate` > 0, p.`dealer_rate`, p.`customer_rate`),
    l.`amount` = IF(p.`dealer_rate` > 0, p.`dealer_rate`, p.`customer_rate`) * l.`qty`
WHERE s.`dealer_id` IS NOT NULL
  AND s.`is_deleted` = 0
  AND (s.`pi_no` IS NULL OR s.`pi_no` = '')
  AND (s.`inv_no` IS NULL OR s.`inv_no` = '')
  AND NOT EXISTS (SELECT 1 FROM `service_document_lines` d WHERE d.`service_job_id` = s.`id`)
  AND l.`line_type` IN ('Component', 'Replacement')
  AND l.`part_id` IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "added_on_doc_no",
                table: "service_lines");
        }
    }
}
