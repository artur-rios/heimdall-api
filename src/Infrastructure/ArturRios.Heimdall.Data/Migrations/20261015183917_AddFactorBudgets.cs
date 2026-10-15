using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds the two per-account factor budgets to <c>heimdall.person</c>: second-factor failures at
    ///     sign-in, with the lock they lead to and how many locks there have been (FR-AU-12), and
    ///     failed re-authentications under a bearer token, with their window and pause (FR-AU-13).
    /// </summary>
    /// <remarks>
    ///     Every existing row starts with nothing charged: the counters at zero, no lock, no window and
    ///     no pause. The columns are written only by targeted writes, never by a whole-row save, and the
    ///     <c>person</c> table's triggers guard <c>public_id</c> alone (#199), so they stay writable.
    /// </remarks>
    public partial class AddFactorBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "failed_reauthentication_attempts",
                schema: "heimdall",
                table: "person",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "failed_second_factor_attempts",
                schema: "heimdall",
                table: "person",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "reauthentication_paused_until",
                schema: "heimdall",
                table: "person",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "reauthentication_window_started_at",
                schema: "heimdall",
                table: "person",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "second_factor_locked_out_until",
                schema: "heimdall",
                table: "person",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "second_factor_lockouts",
                schema: "heimdall",
                table: "person",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "failed_reauthentication_attempts",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "failed_second_factor_attempts",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "reauthentication_paused_until",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "reauthentication_window_started_at",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "second_factor_locked_out_until",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "second_factor_lockouts",
                schema: "heimdall",
                table: "person");
        }
    }
}
