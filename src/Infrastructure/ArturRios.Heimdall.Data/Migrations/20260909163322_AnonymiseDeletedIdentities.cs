using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds the deletion metadata NFR-20's retention window is measured from: when a record was
    ///     logically deleted, why, and whether it has since been anonymised.
    /// </summary>
    public partial class AnonymiseDeletedIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "anonymised_at",
                schema: "heimdall",
                table: "person",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                schema: "heimdall",
                table: "person",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "deletion_kind",
                schema: "heimdall",
                table: "person",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "anonymised_at",
                schema: "heimdall",
                table: "google_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                schema: "heimdall",
                table: "google_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "deletion_kind",
                schema: "heimdall",
                table: "google_user",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_person_deleted_at",
                schema: "heimdall",
                table: "person",
                column: "deleted_at",
                filter: "is_deleted = true AND anonymised_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_google_user_deleted_at",
                schema: "heimdall",
                table: "google_user",
                column: "deleted_at",
                filter: "is_deleted = true AND anonymised_at IS NULL");

            // Rows logically deleted before this migration carry no deletion date, and the
            // anonymisation pass deliberately skips a record whose window cannot be shown to have
            // elapsed. Without a backfill those rows would be the one set the schedule never
            // reaches — exactly the indefinitely retained data NFR-20 exists to end.
            //
            // updated_at is the only evidence available. It is a proxy, not the true deletion date:
            // the logical deletes stamp it, but any later write to the row moves it. That makes it
            // equal to or later than the real deletion, never earlier, so the backfilled window
            // closes on time or late and can never erase a record early. Erring late is the safe
            // direction here; erring early would destroy data before its window was up.
            //
            // The kind is Administrative because it is the only one that could have been recorded:
            // there was no way to request an erasure before this, so no row can be a subject
            // request, and assuming otherwise would apply the shorter statutory deadline to
            // deletions nobody asked for.
            migrationBuilder.Sql(
                """
                UPDATE heimdall.person
                SET deleted_at = updated_at, deletion_kind = 1
                WHERE is_deleted = true AND deleted_at IS NULL;
                """);

            migrationBuilder.Sql(
                """
                UPDATE heimdall.google_user
                SET deleted_at = updated_at, deletion_kind = 1
                WHERE is_deleted = true AND deleted_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_person_deleted_at",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropIndex(
                name: "ix_google_user_deleted_at",
                schema: "heimdall",
                table: "google_user");

            migrationBuilder.DropColumn(
                name: "anonymised_at",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "deletion_kind",
                schema: "heimdall",
                table: "person");

            migrationBuilder.DropColumn(
                name: "anonymised_at",
                schema: "heimdall",
                table: "google_user");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                schema: "heimdall",
                table: "google_user");

            migrationBuilder.DropColumn(
                name: "deletion_kind",
                schema: "heimdall",
                table: "google_user");
        }
    }
}
