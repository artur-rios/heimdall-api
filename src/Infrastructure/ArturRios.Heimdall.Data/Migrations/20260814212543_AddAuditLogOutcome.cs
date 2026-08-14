using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Records the outcome of each audited operation, so the trail covers refusals as well as
    ///     writes (NFR-09).
    /// </summary>
    /// <remarks>
    ///     Every row that exists before this migration was written by the decorator's success-only
    ///     path, so the backfill below is a statement of fact rather than a default: those entries
    ///     record operations that succeeded. Leaving them at the column default would have said the
    ///     opposite about every write the system has ever made.
    /// </remarks>
    public partial class AddAuditLogOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                schema: "heimdall",
                table: "audit_log",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "succeeded",
                schema: "heimdall",
                table: "audit_log",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE heimdall.audit_log SET succeeded = true;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "failure_reason",
                schema: "heimdall",
                table: "audit_log");

            migrationBuilder.DropColumn(
                name: "succeeded",
                schema: "heimdall",
                table: "audit_log");
        }
    }
}
