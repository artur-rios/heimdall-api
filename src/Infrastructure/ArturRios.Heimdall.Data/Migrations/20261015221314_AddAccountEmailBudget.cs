using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds the per-person account-email budget to <c>heimdall.person</c> (FR-PE-16): how many
    ///     account emails the person's own requests had sent in the current one-day window, when that
    ///     window ends, and when the last was sent, which the cooldown counts from.
    /// </summary>
    /// <remarks>
    ///     Every existing row starts with nothing sent and no window. Like the factor budgets, the
    ///     columns are written only by targeted writes, never by a whole-row save.
    /// </remarks>
    public partial class AddAccountEmailBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "account_email_last_sent_at",
                schema: "heimdall",
                table: "person",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "account_email_window_ends_at",
                schema: "heimdall",
                table: "person",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "account_emails_sent",
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
                name: "account_email_last_sent_at",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "account_email_window_ends_at",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "account_emails_sent",
                schema: "heimdall",
                table: "person");
        }
    }
}
