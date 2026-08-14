using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds PERSON.scope_id — a copy of the owning SCOPE_USER row's scope — and the partial
    ///     unique index it exists to make possible, closing the last email-uniqueness rule that
    ///     nothing but a check-then-insert enforced (FR-PE-09).
    /// </summary>
    /// <remarks>
    ///     The scope of a User lives in SCOPE_USER and the address in PERSON, and a PostgreSQL unique
    ///     index covers one table — so the per-scope rule could only ever be a read followed by a
    ///     write, which two concurrent creates both pass. The loser of that race became a person who
    ///     could never authenticate, because UC-11's lookup resolves one row and stops.
    ///
    ///     A trigger would not have helped: under READ COMMITTED both inserts see the same pre-write
    ///     snapshot, so both would find the address free. Only a unique index serialises them, and an
    ///     index needs the columns on one table.
    /// </remarks>
    public partial class AddPersonScopeId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "scope_id",
                schema: "heimdall",
                table: "person",
                type: "bigint",
                nullable: true);

            // Backfill from the relationship this column copies, so the index below can be created
            // over existing data rather than only over rows written from here on.
            migrationBuilder.Sql(
                """
                UPDATE heimdall.person AS p
                SET scope_id = su.scope_id
                FROM heimdall.scope_user AS su
                WHERE su.person_id = p.id;
                """);

            // FR-PE-09, User half. The condition matches CreateUserCommandHandler's check term for
            // term — the scope, a case-insensitive address, role User (3), and not logically deleted
            // — so the rule does not change; it simply becomes one the database can hold under
            // concurrency. A logically deleted User drops out of the index exactly as they drop out
            // of the handler's check, which is what keeps their address reusable.
            //
            // As with ux_person_admin_email, this fails on a database that already violates the
            // rule. That is the point: the duplicates have to be resolved before the invariant can
            // be enforced, and creating the index conditionally would leave it unenforced silently.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ux_person_scope_user_email
                    ON heimdall.person (scope_id, LOWER(email))
                    WHERE role_id = 3 AND is_deleted = false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX heimdall.ux_person_scope_user_email;");

            migrationBuilder.DropColumn(
                name: "scope_id",
                schema: "heimdall",
                table: "person");
        }
    }
}
