using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds the guesses made at the outstanding challenge with an app code or a recovery code
    ///     (FR-2F-17), which UC-38 caps at five, as FR-2F-13 caps an email code.
    /// </summary>
    /// <remarks>
    ///     Additive and safe on a live table: a non-null integer with a constant default is a
    ///     catalogue-only change in PostgreSQL 11 and later — no rewrite, no long lock. Existing rows
    ///     start at zero, so a challenge outstanding at deploy time keeps its whole budget.
    /// </remarks>
    public partial class CapChallengeGuesses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "challenge_attempts",
                schema: "heimdall",
                table: "two_factor_auth",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "challenge_attempts",
                schema: "heimdall",
                table: "two_factor_auth");
        }
    }
}
