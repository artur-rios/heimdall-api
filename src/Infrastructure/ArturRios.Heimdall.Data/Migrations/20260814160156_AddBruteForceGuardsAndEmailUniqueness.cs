using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds the per-account brute-force counters (FR-AU-09, FR-2F-13) and the TOTP replay guard
    ///     (FR-2F-09), and turns the two email-uniqueness rules a unique index can express into
    ///     actual unique indexes (FR-PE-09, FR-GO-07).
    /// </summary>
    /// <remarks>
    ///     The indexes are raw SQL because EF Core models neither an index over an expression
    ///     (LOWER(email)) nor one filtered on a role set, and both properties matter: the application
    ///     compares addresses case-insensitively, so an index over the raw column would enforce a
    ///     different rule than the handlers apply, and an administrator's address only has to be
    ///     unique among live administrators.
    /// </remarks>
    public partial class AddBruteForceGuardsAndEmailUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "failed_attempts",
                schema: "heimdall",
                table: "two_factor_email_code",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "last_totp_time_step_used",
                schema: "heimdall",
                table: "two_factor_auth",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "failed_login_attempts",
                schema: "heimdall",
                table: "person",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "locked_out_until",
                schema: "heimdall",
                table: "person",
                type: "timestamp with time zone",
                nullable: true);

            // FR-GO-07, Google User half: the address is unique within the scope, compared the way
            // the application compares it. The index this replaces was over the raw column, so
            // "bob@x.com" and "Bob@x.com" were two free addresses to the database and one taken
            // address to GoogleSignInCommandHandler.
            //
            // Logically deleted rows are deliberately included, matching the handler: FR-GO-12 keeps
            // a deleted Google User addressable rather than erasing it, so the address it holds stays
            // taken until UC-29 removes the row for good.
            migrationBuilder.Sql(
                """
                DROP INDEX heimdall.ix_google_user_scope_id_email;

                CREATE UNIQUE INDEX ix_google_user_scope_id_email
                    ON heimdall.google_user (scope_id, LOWER(email));
                """);

            // FR-PE-09, administrator half: unique system-wide across live ScopeAdmins (role 2) and
            // SystemAdmins (role 1). This is the enforcement, not a duplicate of the handlers' check
            // — a check-then-insert cannot be, since two concurrent creates both read "free" before
            // either writes, and the loser of that race becomes a person who can never log in
            // (UC-11's admin lookup resolves one row and stops).
            //
            // If this statement fails on an existing database, the data already violates FR-PE-09 and
            // the duplicates have to be resolved before the API can enforce it. Failing the migration
            // is the point: creating the index "if possible" would leave the invariant unenforced
            // with nothing to say so.
            //
            // The User half of FR-PE-09 — unique per scope — is not here and cannot be: the scope
            // lives in SCOPE_USER, and a PostgreSQL index covers one table. It stays enforced in the
            // application layer; see PersonDbMap for what closing its race would require.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ux_person_admin_email
                    ON heimdall.person (LOWER(email))
                    WHERE role_id IN (1, 2) AND is_deleted = false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX heimdall.ux_person_admin_email;

                DROP INDEX heimdall.ix_google_user_scope_id_email;

                CREATE UNIQUE INDEX ix_google_user_scope_id_email
                    ON heimdall.google_user (scope_id, email);
                """);

            migrationBuilder.DropColumn(
                name: "failed_attempts",
                schema: "heimdall",
                table: "two_factor_email_code");

            migrationBuilder.DropColumn(
                name: "last_totp_time_step_used",
                schema: "heimdall",
                table: "two_factor_auth");

            migrationBuilder.DropColumn(
                name: "failed_login_attempts",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "locked_out_until",
                schema: "heimdall",
                table: "person");
        }
    }
}
