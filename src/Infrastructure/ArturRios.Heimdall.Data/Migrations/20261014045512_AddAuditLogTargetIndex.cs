using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Indexes AUDIT_LOG.target_id, so UC-48's filter by affected entity (FR-AT-01) reads an index
    ///     rather than the whole trail.
    /// </summary>
    /// <remarks>
    ///     Adding an index is DDL, not a row change, so the append-only triggers installed by
    ///     MakeAuditLogAppendOnly and AllowClearingAuditActor neither see it nor need changing. No
    ///     column is added, so the record of processing (Data Protection Document §5.1) is unchanged.
    /// </remarks>
    public partial class AddAuditLogTargetIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_audit_log_target_id",
                schema: "heimdall",
                table: "audit_log",
                column: "target_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_log_target_id",
                schema: "heimdall",
                table: "audit_log");
        }
    }
}
