using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInwardRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "roles",
                columns: new[] { "id", "description", "name" },
                values: new object[] { 10, null, "inward" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop assignments first or the role delete FK-fails, same as the earlier
            // RemoveSubRolesAndAddConsignee migration had to do.
            migrationBuilder.Sql("DELETE FROM `user_roles` WHERE `role_id` = 10;");

            migrationBuilder.DeleteData(
                table: "roles",
                keyColumn: "id",
                keyValue: 10);
        }
    }
}
