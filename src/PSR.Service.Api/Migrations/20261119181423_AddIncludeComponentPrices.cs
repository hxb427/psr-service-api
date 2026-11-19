using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddIncludeComponentPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "include_component_prices",
                table: "services",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // Every job already priced by a PI or an invoice was priced WITH its components — that was
            // the only way there was. Left at the new default, a job with a PI and no invoice yet would
            // be invoiced for less than its PI quoted (and refused outright with line editing switched
            // off), and the setting is fixed from the PI onwards so nobody could put it right. Jobs not
            // yet on a PI take the new default and bill their service charges only.
            migrationBuilder.Sql(
                "UPDATE `services` SET `include_component_prices` = 1 "
                + "WHERE (`pi_no` IS NOT NULL AND `pi_no` <> '') OR (`inv_no` IS NOT NULL AND `inv_no` <> '');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "include_component_prices",
                table: "services");
        }
    }
}
