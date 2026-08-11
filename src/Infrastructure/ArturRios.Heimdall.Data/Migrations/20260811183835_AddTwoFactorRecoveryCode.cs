using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTwoFactorRecoveryCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "two_factor_recovery_code",
                schema: "heimdall",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    two_factor_auth_id = table.Column<long>(type: "bigint", nullable: false),
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    used = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_two_factor_recovery_code", x => x.id);
                    table.ForeignKey(
                        name: "fk_two_factor_recovery_code_two_factor_auth_two_factor_auth_id",
                        column: x => x.two_factor_auth_id,
                        principalSchema: "heimdall",
                        principalTable: "two_factor_auth",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_two_factor_recovery_code_two_factor_auth_id",
                schema: "heimdall",
                table: "two_factor_recovery_code",
                column: "two_factor_auth_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "two_factor_recovery_code",
                schema: "heimdall");
        }
    }
}
