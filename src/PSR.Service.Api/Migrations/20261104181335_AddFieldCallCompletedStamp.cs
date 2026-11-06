using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <summary>Gives the call register a column that means "finished", and backfills it as well as
    /// the old rows allow.
    ///
    /// Until now nothing recorded WHEN a call closed. updated_at is the last touch and not the
    /// completion, and a register with no history table had nowhere else to put it - so the office
    /// turnaround figure had no honest source. See FieldCall.CompletedAt.</summary>
    public partial class AddFieldCallCompletedStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "completed_at",
                table: "field_calls",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_calls_call_status_created_at",
                table: "field_calls",
                columns: new[] { "call_status", "created_at" });

            // Backfill. Without it every call completed before today would carry no completion moment
            // and drop out of the turnaround figure entirely, which would make the first reading of
            // that figure cover only the calls closed since the deploy.
            //
            // updated_at is the closest thing the old rows hold: for a call logged open and later
            // closed it IS the moment it closed - unless somebody corrected the call afterwards, in
            // which case it is late by however long that took. created_at covers the rest, where
            // updated_at is null: a call logged Complete on the way in was finished before anybody
            // wrote it down, so nil turnaround is the right answer rather than a guess.
            //
            // A turnaround figure over calls from before this migration is therefore an estimate, and
            // one over calls logged after it is a reading. No backfill could do better - the exact
            // moment survives only as prose in the audit log.
            migrationBuilder.Sql(@"
UPDATE `field_calls`
SET `completed_at` = COALESCE(`updated_at`, `created_at`)
WHERE `call_status` = 'Complete' AND `completed_at` IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_field_calls_call_status_created_at",
                table: "field_calls");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "field_calls");
        }
    }
}
