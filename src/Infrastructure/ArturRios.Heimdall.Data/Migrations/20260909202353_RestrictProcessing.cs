using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds the restriction-of-processing state (NFR-24), independent of logical deletion.
    /// </summary>
    /// <remarks>
    ///     Nothing is backfilled: no identity was restricted before this existed, because there was
    ///     no way to be. Unlike the lawful basis, where every existing row genuinely had one that
    ///     simply went unrecorded, an absent restriction here is the true state rather than an
    ///     unknown one.
    /// </remarks>
    public partial class RestrictProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "processing_restricted_at",
                schema: "heimdall",
                table: "person",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "restriction_ground",
                schema: "heimdall",
                table: "person",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "restriction_lift_notified_at",
                schema: "heimdall",
                table: "person",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "processing_restricted_at",
                schema: "heimdall",
                table: "google_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "restriction_ground",
                schema: "heimdall",
                table: "google_user",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "restriction_lift_notified_at",
                schema: "heimdall",
                table: "google_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_person_processing_restricted_at",
                schema: "heimdall",
                table: "person",
                column: "processing_restricted_at",
                filter: "processing_restricted_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_google_user_processing_restricted_at",
                schema: "heimdall",
                table: "google_user",
                column: "processing_restricted_at",
                filter: "processing_restricted_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_person_processing_restricted_at",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropIndex(
                name: "ix_google_user_processing_restricted_at",
                schema: "heimdall",
                table: "google_user");

            migrationBuilder.DropColumn(
                name: "processing_restricted_at",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "restriction_ground",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "restriction_lift_notified_at",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "processing_restricted_at",
                schema: "heimdall",
                table: "google_user");

            migrationBuilder.DropColumn(
                name: "restriction_ground",
                schema: "heimdall",
                table: "google_user");

            migrationBuilder.DropColumn(
                name: "restriction_lift_notified_at",
                schema: "heimdall",
                table: "google_user");
        }
    }
}
