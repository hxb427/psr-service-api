using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <summary>Repairs the users whose optional text columns hold "" where they should hold NULL.
    ///
    /// No schema change - this is the data half of a fix whose other half is in UsersEndpoints,
    /// where the create path now normalises a blank to null the way the update path always did.
    /// Without this pass the rows already written stay broken: every screen resolves a person's name
    /// as `FullName ?? Username`, a coalesce only catches NULL, so a user created with the name box
    /// left empty appeared as an empty cell wherever their name belonged - the technician and
    /// engineer columns on the field activity page among them. Editing such a user happened to
    /// repair them, which is why it showed on some records and not others.
    ///
    /// Email goes through the same pass. Nothing reads it with a fallback today, but it is the same
    /// kind of column written by the same line, and leaving one of the pair holding "" would only
    /// mean doing this again later.</summary>
    public partial class NormaliseBlankUserNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE `users`
SET `full_name` = NULL
WHERE `full_name` IS NOT NULL AND TRIM(`full_name`) = ''");

            migrationBuilder.Sql(@"
UPDATE `users`
SET `email` = NULL
WHERE `email` IS NOT NULL AND TRIM(`email`) = ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. "" and NULL meant the same thing to every reader of these columns,
            // so there is nothing to restore - and writing "" back over the NULLs would hit the rows
            // that were always NULL too, the two being indistinguishable once this has run.
        }
    }
}
