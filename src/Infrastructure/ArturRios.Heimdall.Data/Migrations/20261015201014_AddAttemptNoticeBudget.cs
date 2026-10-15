using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds <c>heimdall.attempt_notice_budget</c>: per address, the registration-attempt notices sent
    ///     in its current one-day window (FR-SR-14). The address is held only as a SHA-256 digest.
    /// </summary>
    /// <remarks>
    ///     Starts empty: an address no notice has been charged to has no row, and its first notice
    ///     inserts one. The unique index on <c>address_hash</c> is what the charge's
    ///     <c>INSERT … ON CONFLICT</c> resolves against; the index on <c>window_ends_at</c> serves the
    ///     purge.
    /// </remarks>
    public partial class AddAttemptNoticeBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attempt_notice_budget",
                schema: "heimdall",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    address_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    notices_sent = table.Column<int>(type: "integer", nullable: false),
                    window_ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attempt_notice_budget", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attempt_notice_budget_address_hash",
                schema: "heimdall",
                table: "attempt_notice_budget",
                column: "address_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attempt_notice_budget_window_ends_at",
                schema: "heimdall",
                table: "attempt_notice_budget",
                column: "window_ends_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attempt_notice_budget",
                schema: "heimdall");
        }
    }
}
