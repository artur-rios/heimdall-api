using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Indexes PERSON by <c>LOWER(email)</c> and drops the index on the raw column, so the login,
    ///     password-recovery, registration and Google sign-in lookups no longer scan the table (#207).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Those lookups compare <c>LOWER(email)</c> and deliberately leave out <c>is_deleted</c>, so
    ///         that a deleted person is found and refused. <c>ix_person_email</c> on the raw column cannot
    ///         serve that expression, and the two partial unique indexes on <c>LOWER(email)</c> require
    ///         <c>is_deleted = false</c>, which the query does not state. PostgreSQL read the whole table,
    ///         before the password hash, on the two hottest anonymous endpoints. The old index served no
    ///         query and cost a write on every insert and update, so it goes.
    ///     </para>
    ///     <para>
    ///         Built with <c>CREATE INDEX CONCURRENTLY</c>, outside the migration's transaction, so
    ///         sign-ups and updates are not blocked while it reads the table. <c>IF NOT EXISTS</c> lets a
    ///         build interrupted part-way be run again; one that failed leaves an invalid index, which an
    ///         operator drops before re-running. On the way down the old index is built before the new
    ///         one is dropped. EF cannot express an index on an expression, so the model snapshot
    ///         knows neither this index nor the partial ones; it only loses <c>ix_person_email</c>.
    ///     </para>
    /// </remarks>
    public partial class AddPersonLowerEmailIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_person_lower_email
                    ON heimdall.person (LOWER(email));
                """,
                suppressTransaction: true);

            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS heimdall.ix_person_email;",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_person_email
                    ON heimdall.person (email);
                """,
                suppressTransaction: true);

            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS heimdall.ix_person_lower_email;",
                suppressTransaction: true);
        }
    }
}
