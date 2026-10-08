using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds the configuration's outstanding challenge (FR-2F-10), which the challenge token names
    ///     and UC-38 clears on redemption, so a challenge can be redeemed once.
    /// </summary>
    /// <remarks>
    ///     Additive and nullable, so it is safe on a live table. Existing rows start with none
    ///     outstanding, which means a challenge issued by the previous release — whose token carries
    ///     no challenge claim — is refused after the upgrade: at most ten minutes of in-flight 2FA
    ///     logins have to sign in again.
    /// </remarks>
    public partial class TrackOutstandingChallenge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "challenge_id",
                schema: "heimdall",
                table: "two_factor_auth",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "challenge_id",
                schema: "heimdall",
                table: "two_factor_auth");
        }
    }
}
