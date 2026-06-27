using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSubRolesAndAddConsignee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop any assignments of the removed roles first so the role deletes can't FK-fail.
            migrationBuilder.Sql("DELETE FROM `user_roles` WHERE `role_id` IN (5, 7);");

            migrationBuilder.DeleteData(
                table: "roles",
                keyColumn: "id",
                keyValue: 5);

            migrationBuilder.DeleteData(
                table: "roles",
                keyColumn: "id",
                keyValue: 7);

            migrationBuilder.AddColumn<string>(
                name: "consignee_address",
                table: "service_documents",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "consignee_address",
                table: "service_documents");

            migrationBuilder.InsertData(
                table: "roles",
                columns: new[] { "id", "description", "name" },
                values: new object[,]
                {
                    { 5, null, "inward_manager" },
                    { 7, null, "dispatch_manager" }
                });
        }
    }
}
