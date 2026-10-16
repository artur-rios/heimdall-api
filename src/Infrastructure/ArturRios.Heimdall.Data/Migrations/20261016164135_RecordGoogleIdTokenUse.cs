using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds <c>heimdall.google_id_token_use</c> (FR-GO-21): one row per Google ID token the system
    ///     has accepted, keyed by the SHA-256 digest of its <c>jti</c> (or of its signed part when it
    ///     has none), with the token's expiry, which the purge counts from (NFR-19).
    /// </summary>
    /// <remarks>
    ///     Starts empty: tokens accepted before it existed were never recorded, and every one of them
    ///     expired within the hour Google signs a token for.
    /// </remarks>
    public partial class RecordGoogleIdTokenUse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "google_id_token_use",
                schema: "heimdall",
                columns: table => new
                {
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_google_id_token_use", x => x.token_hash);
                });

            migrationBuilder.CreateIndex(
                name: "ix_google_id_token_use_expires_at",
                schema: "heimdall",
                table: "google_id_token_use",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "google_id_token_use",
                schema: "heimdall");
        }
    }
}
