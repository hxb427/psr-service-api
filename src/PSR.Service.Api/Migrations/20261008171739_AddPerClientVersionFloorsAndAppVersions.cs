using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPerClientVersionFloorsAndAppVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_versions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    client_id = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    version = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    version_code = table.Column<int>(type: "int", nullable: false),
                    apk_s3_key = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    notes = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    mandatory = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: true),
                    published_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_versions", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "app_settings",
                columns: new[] { "key", "value" },
                values: new object[] { "min_field_portal_version", "0.0.0" });

            migrationBuilder.CreateIndex(
                name: "IX_app_versions_client_id_is_active_version_code",
                table: "app_versions",
                columns: new[] { "client_id", "is_active", "version_code" });

            migrationBuilder.CreateIndex(
                name: "IX_app_versions_client_id_version_code",
                table: "app_versions",
                columns: new[] { "client_id", "version_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_versions");

            migrationBuilder.DeleteData(
                table: "app_settings",
                keyColumn: "key",
                keyValue: "min_field_portal_version");
        }
    }
}
