using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSerialTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_field_technician",
                table: "users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "component_serials",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    serial_number = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    item_name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    owner_type = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    owner_ref = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    technician_id = table.Column<long>(type: "bigint", nullable: true),
                    last_updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_component_serials", x => x.id);
                    table.ForeignKey(
                        name: "FK_component_serials_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "serial_status_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    component_serial_id = table.Column<long>(type: "bigint", nullable: false),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    serial_number = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    old_status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    new_status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    changed_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                    remarks = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    changed_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_serial_status_history", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_component_serials_owner_type_technician_id",
                table: "component_serials",
                columns: new[] { "owner_type", "technician_id" });

            migrationBuilder.CreateIndex(
                name: "IX_component_serials_part_id_serial_number",
                table: "component_serials",
                columns: new[] { "part_id", "serial_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_component_serials_status",
                table: "component_serials",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_serial_status_history_changed_at",
                table: "serial_status_history",
                column: "changed_at");

            migrationBuilder.CreateIndex(
                name: "IX_serial_status_history_component_serial_id",
                table: "serial_status_history",
                column: "component_serial_id");

            migrationBuilder.CreateIndex(
                name: "IX_serial_status_history_serial_number",
                table: "serial_status_history",
                column: "serial_number");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "component_serials");

            migrationBuilder.DropTable(
                name: "serial_status_history");

            migrationBuilder.DropColumn(
                name: "is_field_technician",
                table: "users");
        }
    }
}
