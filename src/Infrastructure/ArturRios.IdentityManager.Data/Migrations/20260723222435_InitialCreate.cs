using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.IdentityManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity_manager");

            migrationBuilder.CreateTable(
                name: "role",
                schema: "identity_manager",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scope",
                schema: "identity_manager",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    google_sign_in_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scope", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "person",
                schema: "identity_manager",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    password_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    salt = table.Column<byte[]>(type: "bytea", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    email_verified = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    role_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_person", x => x.id);
                    table.ForeignKey(
                        name: "fk_person_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity_manager",
                        principalTable: "role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "google_user",
                schema: "identity_manager",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    google_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    email_verified = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    profile_picture_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    scope_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_google_user", x => x.id);
                    table.ForeignKey(
                        name: "fk_google_user_scope_scope_id",
                        column: x => x.scope_id,
                        principalSchema: "identity_manager",
                        principalTable: "scope",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "application",
                schema: "identity_manager",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    scope_id = table.Column<long>(type: "bigint", nullable: false),
                    owner_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application", x => x.id);
                    table.ForeignKey(
                        name: "fk_application_person_owner_id",
                        column: x => x.owner_id,
                        principalSchema: "identity_manager",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_application_scope_scope_id",
                        column: x => x.scope_id,
                        principalSchema: "identity_manager",
                        principalTable: "scope",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "email_verification_token",
                schema: "identity_manager",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    person_id = table.Column<long>(type: "bigint", nullable: false),
                    token = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_verification_token", x => x.id);
                    table.ForeignKey(
                        name: "fk_email_verification_token_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "identity_manager",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "password_reset_token",
                schema: "identity_manager",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    person_id = table.Column<long>(type: "bigint", nullable: false),
                    token = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_password_reset_token", x => x.id);
                    table.ForeignKey(
                        name: "fk_password_reset_token_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "identity_manager",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scope_owner",
                schema: "identity_manager",
                columns: table => new
                {
                    scope_id = table.Column<long>(type: "bigint", nullable: false),
                    person_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scope_owner", x => new { x.scope_id, x.person_id });
                    table.ForeignKey(
                        name: "fk_scope_owner_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "identity_manager",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_scope_owner_scope_scope_id",
                        column: x => x.scope_id,
                        principalSchema: "identity_manager",
                        principalTable: "scope",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scope_user",
                schema: "identity_manager",
                columns: table => new
                {
                    scope_id = table.Column<long>(type: "bigint", nullable: false),
                    person_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scope_user", x => new { x.scope_id, x.person_id });
                    table.ForeignKey(
                        name: "fk_scope_user_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "identity_manager",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_scope_user_scope_scope_id",
                        column: x => x.scope_id,
                        principalSchema: "identity_manager",
                        principalTable: "scope",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_application_owner_id",
                schema: "identity_manager",
                table: "application",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_application_public_id",
                schema: "identity_manager",
                table: "application",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_application_scope_id",
                schema: "identity_manager",
                table: "application",
                column: "scope_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_verification_token_person_id",
                schema: "identity_manager",
                table: "email_verification_token",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_verification_token_token",
                schema: "identity_manager",
                table: "email_verification_token",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_google_user_public_id",
                schema: "identity_manager",
                table: "google_user",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_google_user_scope_id_email",
                schema: "identity_manager",
                table: "google_user",
                columns: new[] { "scope_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_google_user_scope_id_google_id",
                schema: "identity_manager",
                table: "google_user",
                columns: new[] { "scope_id", "google_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_token_person_id",
                schema: "identity_manager",
                table: "password_reset_token",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_token_token",
                schema: "identity_manager",
                table: "password_reset_token",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_person_email",
                schema: "identity_manager",
                table: "person",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "ix_person_public_id",
                schema: "identity_manager",
                table: "person",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_person_role_id",
                schema: "identity_manager",
                table: "person",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_name",
                schema: "identity_manager",
                table: "role",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_role_public_id",
                schema: "identity_manager",
                table: "role",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scope_name",
                schema: "identity_manager",
                table: "scope",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scope_public_id",
                schema: "identity_manager",
                table: "scope",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scope_owner_person_id",
                schema: "identity_manager",
                table: "scope_owner",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_scope_user_person_id",
                schema: "identity_manager",
                table: "scope_user",
                column: "person_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application",
                schema: "identity_manager");

            migrationBuilder.DropTable(
                name: "email_verification_token",
                schema: "identity_manager");

            migrationBuilder.DropTable(
                name: "google_user",
                schema: "identity_manager");

            migrationBuilder.DropTable(
                name: "password_reset_token",
                schema: "identity_manager");

            migrationBuilder.DropTable(
                name: "scope_owner",
                schema: "identity_manager");

            migrationBuilder.DropTable(
                name: "scope_user",
                schema: "identity_manager");

            migrationBuilder.DropTable(
                name: "person",
                schema: "identity_manager");

            migrationBuilder.DropTable(
                name: "scope",
                schema: "identity_manager");

            migrationBuilder.DropTable(
                name: "role",
                schema: "identity_manager");
        }
    }
}
