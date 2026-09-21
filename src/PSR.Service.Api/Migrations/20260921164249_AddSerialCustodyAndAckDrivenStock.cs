using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSerialCustodyAndAckDrivenStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "sender_debited_on_send",
                table: "technician_transfers",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "stock_returns",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "GoodStock")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "technician_debited_on_ship",
                table: "stock_returns",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "credited_on_issue",
                table: "stock_movements",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<long>(
                name: "source_component_serial_id",
                table: "services",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "customer_id",
                table: "field_services",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "customer_id",
                table: "field_sales",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "current_service_job_id",
                table: "component_serials",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "customer_id",
                table: "component_serials",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_component_serials_current_service_job_id",
                table: "component_serials",
                column: "current_service_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_component_serials_customer_id",
                table: "component_serials",
                column: "customer_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_component_serials_current_service_job_id",
                table: "component_serials");

            migrationBuilder.DropIndex(
                name: "IX_component_serials_customer_id",
                table: "component_serials");

            migrationBuilder.DropColumn(
                name: "sender_debited_on_send",
                table: "technician_transfers");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "stock_returns");

            migrationBuilder.DropColumn(
                name: "technician_debited_on_ship",
                table: "stock_returns");

            migrationBuilder.DropColumn(
                name: "credited_on_issue",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "source_component_serial_id",
                table: "services");

            migrationBuilder.DropColumn(
                name: "customer_id",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "customer_id",
                table: "field_sales");

            migrationBuilder.DropColumn(
                name: "current_service_job_id",
                table: "component_serials");

            migrationBuilder.DropColumn(
                name: "customer_id",
                table: "component_serials");
        }
    }
}
