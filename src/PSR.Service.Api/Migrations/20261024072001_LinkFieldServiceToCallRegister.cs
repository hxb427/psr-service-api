using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class LinkFieldServiceToCallRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "field_call_id",
                table: "field_services",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_phone",
                table: "field_calls",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "reported_complaint",
                table: "field_calls",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_field_services_field_call_id",
                table: "field_services",
                column: "field_call_id");

            migrationBuilder.AddForeignKey(
                name: "FK_field_services_field_calls_field_call_id",
                table: "field_services",
                column: "field_call_id",
                principalTable: "field_calls",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_field_services_field_calls_field_call_id",
                table: "field_services");

            migrationBuilder.DropIndex(
                name: "IX_field_services_field_call_id",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "field_call_id",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "contact_phone",
                table: "field_calls");

            migrationBuilder.DropColumn(
                name: "reported_complaint",
                table: "field_calls");
        }
    }
}
