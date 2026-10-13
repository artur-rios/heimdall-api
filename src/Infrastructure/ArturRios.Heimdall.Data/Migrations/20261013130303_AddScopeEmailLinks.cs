using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScopeEmailLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "email_verification_url",
                schema: "heimdall",
                table: "scope",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "password_reset_url",
                schema: "heimdall",
                table: "scope",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "email_verification_url",
                schema: "heimdall",
                table: "scope");

            migrationBuilder.DropColumn(
                name: "password_reset_url",
                schema: "heimdall",
                table: "scope");
        }
    }
}
