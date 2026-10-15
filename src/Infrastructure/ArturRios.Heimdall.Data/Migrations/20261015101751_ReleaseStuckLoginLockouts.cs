using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Releases accounts left at the failed-login limit with no lockout to expire. Data only; the
    ///     schema does not change.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Until now the lock was set after the tenth password comparison completed. A request that
    ///         reserved the tenth attempt and did not finish comparing left <c>failed_login_attempts</c>
    ///         at 10 with <c>locked_out_until</c> NULL, and every later attempt was refused because the
    ///         counter was no longer below the limit. The reserving write now locks the account itself,
    ///         so no new row reaches that state; this resets the ones that already have.
    ///     </para>
    ///     <para>
    ///         The limit is <c>LoginCommandHandler.MaxFailedLoginAttempts</c>, 10. A row at or past it
    ///         with a lockout date is a lock that expires and is left alone. Only the counter is
    ///         written; the <c>person</c> table's triggers guard <c>public_id</c> alone, so no audit or
    ///         identity rule applies. Down does nothing: the rows released here cannot be told apart
    ///         from rows that were never stuck, and restoring the fault would only lock people out.
    ///     </para>
    /// </remarks>
    public partial class ReleaseStuckLoginLockouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE heimdall.person
                SET failed_login_attempts = 0
                WHERE failed_login_attempts >= 10
                  AND locked_out_until IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: see the remarks.
        }
    }
}
