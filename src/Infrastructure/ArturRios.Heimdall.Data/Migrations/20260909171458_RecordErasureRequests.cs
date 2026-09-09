using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordErasureRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "erasure_blocked_reason",
                schema: "heimdall",
                table: "person",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "erasure_due_at",
                schema: "heimdall",
                table: "person",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "erasure_requested_at",
                schema: "heimdall",
                table: "person",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erasure_blocked_reason",
                schema: "heimdall",
                table: "google_user",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "erasure_due_at",
                schema: "heimdall",
                table: "google_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "erasure_requested_at",
                schema: "heimdall",
                table: "google_user",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "erasure_blocked_reason",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "erasure_due_at",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "erasure_requested_at",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "erasure_blocked_reason",
                schema: "heimdall",
                table: "google_user");

            migrationBuilder.DropColumn(
                name: "erasure_due_at",
                schema: "heimdall",
                table: "google_user");

            migrationBuilder.DropColumn(
                name: "erasure_requested_at",
                schema: "heimdall",
                table: "google_user");
        }
    }
}
