using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceReplacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "replacement_part_id",
                table: "services",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "replacement_serial_no",
                table: "services",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_services_replacement_part_id",
                table: "services",
                column: "replacement_part_id");

            migrationBuilder.AddForeignKey(
                name: "FK_services_parts_replacement_part_id",
                table: "services",
                column: "replacement_part_id",
                principalTable: "parts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_services_parts_replacement_part_id",
                table: "services");

            migrationBuilder.DropIndex(
                name: "IX_services_replacement_part_id",
                table: "services");

            migrationBuilder.DropColumn(
                name: "replacement_part_id",
                table: "services");

            migrationBuilder.DropColumn(
                name: "replacement_serial_no",
                table: "services");
        }
    }
}
