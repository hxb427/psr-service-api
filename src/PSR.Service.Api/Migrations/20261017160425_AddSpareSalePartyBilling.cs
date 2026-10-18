using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSpareSalePartyBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "party_address",
                table: "spare_sales",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "party_state",
                table: "spare_sales",
                type: "varchar(60)",
                maxLength: 60,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "party_state_code",
                table: "spare_sales",
                type: "varchar(10)",
                maxLength: 10,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_spare_sales_party_state_code",
                table: "spare_sales",
                column: "party_state_code");

            // The state the sales already on the books were billed in, taken from the dealer master —
            // which is exactly where the generate form has been reading it from all along, so this
            // records what those sales were already being taxed on rather than deciding anything new.
            // Without it the register's statewise filter would answer "nothing" until the day's entries
            // built up again.
            //
            // The code is normalised to the two digits every state comparison is made in (LPAD truncates
            // as well as pads, which matches GstStates.NormalizeCode taking the leading two digits), and
            // a code that does not start with a digit is left alone rather than stored as rubbish.
            //
            // Only the state travels. party_address stays null on purpose: the master's address today is
            // not evidence of what was printed on a document last year, and a null there reads through to
            // the master the way it always has. Direct-customer sales get nothing — the customer master
            // has never carried a state, which is the gap the entry form now closes.
            migrationBuilder.Sql("""
                UPDATE spare_sales s
                  JOIN dealers d ON d.id = s.dealer_id
                   SET s.party_state      = d.state,
                       s.party_state_code = LPAD(d.state_code, 2, '0')
                 WHERE s.dealer_id IS NOT NULL
                   AND d.state_code IS NOT NULL
                   AND d.state_code REGEXP '^[0-9]'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_spare_sales_party_state_code",
                table: "spare_sales");

            migrationBuilder.DropColumn(
                name: "party_address",
                table: "spare_sales");

            migrationBuilder.DropColumn(
                name: "party_state",
                table: "spare_sales");

            migrationBuilder.DropColumn(
                name: "party_state_code",
                table: "spare_sales");
        }
    }
}
