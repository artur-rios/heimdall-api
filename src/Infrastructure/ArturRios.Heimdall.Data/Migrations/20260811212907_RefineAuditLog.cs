using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <inheritdoc />
    public partial class RefineAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "action",
                schema: "heimdall",
                table: "audit_log",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_actor_person_id",
                schema: "heimdall",
                table: "audit_log",
                column: "actor_person_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_created_at",
                schema: "heimdall",
                table: "audit_log",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_log_actor_person_id",
                schema: "heimdall",
                table: "audit_log");

            migrationBuilder.DropIndex(
                name: "ix_audit_log_created_at",
                schema: "heimdall",
                table: "audit_log");

            migrationBuilder.AlterColumn<string>(
                name: "action",
                schema: "heimdall",
                table: "audit_log",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);
        }
    }
}
