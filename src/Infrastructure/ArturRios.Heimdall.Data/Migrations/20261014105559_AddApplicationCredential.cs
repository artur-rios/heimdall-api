using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Creates APPLICATION_CREDENTIAL (UC-53…UC-57, FR-AP-10): the hashed secrets an application
    ///     exchanges for an application token.
    /// </summary>
    /// <remarks>
    ///     A new table with no existing rows, so nothing is backfilled. It holds no personal data —
    ///     an application reference, a SHA-256 and four instants — which the Data Protection
    ///     Document records; the Data Retention Schedule's token row covers its purge.
    /// </remarks>
    public partial class AddApplicationCredential : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "application_credential",
                schema: "heimdall",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<long>(type: "bigint", nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_credential", x => x.id);
                    table.ForeignKey(
                        name: "fk_application_credential_application_application_id",
                        column: x => x.application_id,
                        principalSchema: "heimdall",
                        principalTable: "application",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_application_credential_application_id",
                schema: "heimdall",
                table: "application_credential",
                column: "application_id");

            migrationBuilder.CreateIndex(
                name: "ix_application_credential_public_id",
                schema: "heimdall",
                table: "application_credential",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_application_credential_secret_hash",
                schema: "heimdall",
                table: "application_credential",
                column: "secret_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application_credential",
                schema: "heimdall");
        }
    }
}
