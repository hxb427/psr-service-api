using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddShelfedSerialStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SHELFED is new and it is now the ONLY status the warehouse shelf counts. RETURNED_TO_SC
            // and REPAIRED used to be counted too, and this moves every unit that was on the shelf
            // under the old rule onto the new one.
            //
            // The backfill is load-bearing, not tidying. Every one of these rows reached its status
            // through a path that credited the warehouse by one - a return booked in, a job kept in
            // stock, or a manual change that applied WarehouseDeltaFor. Narrowing the set without
            // moving the rows would take those units off the shelf while their quantity stayed on
            // it: the shelf would under-report, and the next issue of each part would either fail on
            // a guard nobody could explain or run the balance down against a unit sitting in plain
            // sight. That is the precise drift the +1/-1 rule exists to prevent, so this closes it in
            // the same migration that opens it.
            //
            // Owner is part of the condition because it was part of the old rule: an issue only ever
            // accepted a unit that was BOTH in a re-issuable status AND in the service centre's
            // custody (SerialService.FindIssueConflictsAsync). A row in one of these statuses held by
            // someone else was not shelf stock before and does not become it here - it is left exactly
            // as it is, because such a row is a custody bug to look at and not a unit to shelve.
            migrationBuilder.Sql(
                "UPDATE component_serials SET status = 'Shelfed' "
                + "WHERE status IN ('ReturnedToSc', 'Repaired') AND owner_type = 'ServiceCenter';");

            // serial_status_history is deliberately NOT rewritten, which is the opposite of what
            // CollapseInstalledSerialStatusIntoUsed did - and for the opposite reason. That migration
            // removed a word from the enum, so a history row still using it would fail to materialise.
            // These two words both survive: REPAIRED now means a bench has finished with a unit that
            // nobody has put away yet, and RETURNED_TO_SC is kept for the units that genuinely passed
            // through it. A unit that WAS booked in as RETURNED_TO_SC in March was, and its trail
            // should still say so.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back takes SHELFED out of the enum, so a row still saying it would fail to
            // materialise at all - these have to go somewhere. REPAIRED is where, because it is one
            // of the two statuses the old code counted as shelf stock, so the shelf and the warehouse
            // count still agree after the rollback, which is the property worth preserving.
            //
            // What is lost is the split between a unit that came back unused and one that came off a
            // bench: both read as REPAIRED afterwards. That is not recoverable from this table, and
            // guessing it from the ledger would invent a distinction for units that never had one.
            // The history rows do still carry which route each unit actually took.
            migrationBuilder.Sql("UPDATE component_serials SET status = 'Repaired' WHERE status = 'Shelfed';");
            migrationBuilder.Sql("UPDATE serial_status_history SET new_status = 'Repaired' WHERE new_status = 'Shelfed';");
            migrationBuilder.Sql("UPDATE serial_status_history SET old_status = 'Repaired' WHERE old_status = 'Shelfed';");
        }
    }
}
