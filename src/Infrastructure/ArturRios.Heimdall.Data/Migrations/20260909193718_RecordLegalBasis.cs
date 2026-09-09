using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds the lawful basis and privacy notice version recorded against each identity, and the
    ///     tenant-level declaration they are resolved from (NFR-23).
    /// </summary>
    public partial class RecordLegalBasis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "default_legal_basis",
                schema: "heimdall",
                table: "scope",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "privacy_notice_uri",
                schema: "heimdall",
                table: "scope",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "basis_recorded_at",
                schema: "heimdall",
                table: "person",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "legal_basis",
                schema: "heimdall",
                table: "person",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "privacy_notice_version",
                schema: "heimdall",
                table: "person",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "basis_recorded_at",
                schema: "heimdall",
                table: "google_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "legal_basis",
                schema: "heimdall",
                table: "google_user",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "privacy_notice_version",
                schema: "heimdall",
                table: "google_user",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            // Existing rows keep LegalBasis = 0, which the enum names Unrecorded, and a null notice
            // version. That is deliberate and is the whole point of having an Unrecorded member.
            //
            // Contract performance almost certainly applied to every one of them — it is the basis
            // for every identity this system has ever created. Writing it here would nonetheless be
            // asserting, in the record the controller shows a regulator, something nobody checked
            // for those particular rows. Accountability under GDPR Art. 5(2) is the ability to
            // demonstrate the basis, and a backfilled guess demonstrates only that a migration ran.
            //
            // So they are left visibly unrecorded: honest, and obviously a thing to fix rather than
            // silently indistinguishable from a row that was recorded properly.

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_legal_basis",
                schema: "heimdall",
                table: "scope");

            migrationBuilder.DropColumn(
                name: "privacy_notice_uri",
                schema: "heimdall",
                table: "scope");

            migrationBuilder.DropColumn(
                name: "basis_recorded_at",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "legal_basis",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "privacy_notice_version",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "basis_recorded_at",
                schema: "heimdall",
                table: "google_user");

            migrationBuilder.DropColumn(
                name: "legal_basis",
                schema: "heimdall",
                table: "google_user");

            migrationBuilder.DropColumn(
                name: "privacy_notice_version",
                schema: "heimdall",
                table: "google_user");
        }
    }
}
