using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds the count of email-code reissues a challenge has authorized (UC-46, FR-2F-13).
    /// </summary>
    /// <remarks>
    ///     Existing rows default to zero, which is the true state rather than an unknown one: before
    ///     this column existed there was no way to obtain a further code without re-authenticating,
    ///     so no challenge had ever spent a reissue. The counter is reset by every challenge UC-11
    ///     issues, so the value a row carries at any moment describes the authentication attempt in
    ///     progress and nothing older.
    /// </remarks>
    public partial class CapChallengeCodeReissues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "email_code_reissue_count",
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
                name: "email_code_reissue_count",
                schema: "heimdall",
                table: "two_factor_auth");
        }
    }
}
