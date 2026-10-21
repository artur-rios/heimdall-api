using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds two partial indexes on AUDIT_LOG.created_at — over the entries that still have an actor,
    ///     and over those that still have a target — so NFR-21's pass finds the entries past their
    ///     attribution period by reading only those, oldest first, instead of the whole trail (#206).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The pass used to evaluate up to four correlated subqueries for every attributed entry in the
    ///         attribution window, so a run with nothing due still read the whole window, on every
    ///         instance, every run. It now asks each ground separately; the two grounds of age are served by
    ///         these, the grounds of identity by the existing indexes on actor_person_id and target_id. Each
    ///         index is partial, so it holds only the rows its query can still return and shrinks as the
    ///         pass clears them.
    ///     </para>
    ///     <para>
    ///         Built with <c>CREATE INDEX CONCURRENTLY</c>, outside the migration's transaction: audit_log
    ///         takes a row for every command, and a plain build would block those writes for as long as it
    ///         reads the table. <c>IF NOT EXISTS</c> lets a build interrupted part-way be run again; one
    ///         that failed leaves an invalid index, which an operator drops before re-running. Adding an
    ///         index is DDL, not a row change, so the append-only triggers neither see it nor need changing.
    ///     </para>
    /// </remarks>
    public partial class AddAuditRetentionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_audit_log_created_at_attributed
                    ON heimdall.audit_log (created_at)
                    WHERE actor_person_id IS NOT NULL;
                """,
                suppressTransaction: true);

            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_audit_log_created_at_targeted
                    ON heimdall.audit_log (created_at)
                    WHERE target_id IS NOT NULL;
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS heimdall.ix_audit_log_created_at_attributed;",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS heimdall.ix_audit_log_created_at_targeted;",
                suppressTransaction: true);
        }
    }
}
