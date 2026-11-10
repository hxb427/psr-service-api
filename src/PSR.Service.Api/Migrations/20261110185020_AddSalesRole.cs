using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "roles",
                columns: new[] { "id", "description", "name" },
                values: new object[] { 11, null, "sales" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop assignments first or the role delete FK-fails, same as AddInwardRole had to do.
            migrationBuilder.Sql("DELETE FROM `user_roles` WHERE `role_id` = 11;");

            migrationBuilder.DeleteData(
                table: "roles",
                keyColumn: "id",
                keyValue: 11);
        }
    }
}
