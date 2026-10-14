using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds PENDING_REGISTRATION.public_id — the administrative reference by which a scope's owners
    ///     list and discard a self-registration that is waiting for confirmation (UC-49, UC-50) — and
    ///     the unique index that keeps it an identifier.
    /// </summary>
    /// <remarks>
    ///     Existing rows are backfilled with gen_random_uuid(): a constant default, which is what
    ///     scaffolding produces for a non-nullable column, would give every existing row the same id
    ///     and fail the unique index. The default exists only for that backfill and is dropped again,
    ///     so the column matches the model, where the entity supplies the id.
    ///
    ///     The id is random and carries no personal data; its purpose is recorded in §5.1 of the Data
    ///     Protection Document. The row's lifetime is unchanged, so the Data Retention Schedule is too.
    /// </remarks>
    public partial class AddPendingRegistrationPublicId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "public_id",
                schema: "heimdall",
                table: "pending_registration",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.Sql(
                "ALTER TABLE heimdall.pending_registration ALTER COLUMN public_id DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "ix_pending_registration_public_id",
                schema: "heimdall",
                table: "pending_registration",
                column: "public_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pending_registration_public_id",
                schema: "heimdall",
                table: "pending_registration");

            migrationBuilder.DropColumn(
                name: "public_id",
                schema: "heimdall",
                table: "pending_registration");
        }
    }
}
