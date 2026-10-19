using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds <c>heimdall.scope_owner_invitation</c> (UC-21, FR-SC-14): an invitation to co-own a scope,
    ///     answered by the invitee, and puts its <c>public_id</c> in the trigger family that guards the
    ///     other trusted tables (#199).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Starts empty. The partial unique index on <c>(scope_id, invitee_person_id)</c> where
    ///         <c>ended_at</c> is null holds one unended invitation per pair; the two check constraints
    ///         keep <c>outcome</c> to 1–4 and to ended rows. Deleting the scope or the invitee deletes the
    ///         invitation; deleting the inviter sets <c>invited_by_person_id</c> to null.
    ///     </para>
    ///     <para>
    ///         <c>public_id_is_unused()</c> is replaced by the same function with one more branch, so a new
    ///         row of any guarded table may not take an invitation's id and an invitation may not take
    ///         theirs; the two row-level triggers bind it and <c>public_id_is_immutable()</c> to the new
    ///         table. Down drops the triggers, restores the function as <c>ProtectIdentityPublicIds</c>
    ///         left it, and drops the table.
    ///     </para>
    /// </remarks>
    public partial class AddScopeOwnerInvitation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scope_owner_invitation",
                schema: "heimdall",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<long>(type: "bigint", nullable: false),
                    invitee_person_id = table.Column<long>(type: "bigint", nullable: false),
                    invited_by_person_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<short>(type: "smallint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scope_owner_invitation", x => x.id);
                    table.CheckConstraint("ck_scope_owner_invitation_outcome_ends_it", "outcome IS NULL OR ended_at IS NOT NULL");
                    table.CheckConstraint("ck_scope_owner_invitation_outcome_known", "outcome IS NULL OR outcome BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "fk_scope_owner_invitation_person_invited_by_person_id",
                        column: x => x.invited_by_person_id,
                        principalSchema: "heimdall",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_scope_owner_invitation_person_invitee_person_id",
                        column: x => x.invitee_person_id,
                        principalSchema: "heimdall",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_scope_owner_invitation_scope_scope_id",
                        column: x => x.scope_id,
                        principalSchema: "heimdall",
                        principalTable: "scope",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scope_owner_invitation_invited_by_person_id",
                schema: "heimdall",
                table: "scope_owner_invitation",
                column: "invited_by_person_id");

            migrationBuilder.CreateIndex(
                name: "ix_scope_owner_invitation_invitee_person_id",
                schema: "heimdall",
                table: "scope_owner_invitation",
                column: "invitee_person_id");

            migrationBuilder.CreateIndex(
                name: "ix_scope_owner_invitation_public_id",
                schema: "heimdall",
                table: "scope_owner_invitation",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scope_owner_invitation_scope_id_invitee_person_id",
                schema: "heimdall",
                table: "scope_owner_invitation",
                columns: new[] { "scope_id", "invitee_person_id" },
                unique: true,
                filter: "ended_at IS NULL");

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION heimdall.public_id_is_unused()
                RETURNS TRIGGER AS $$
                BEGIN
                    -- A new row may not take an id a row of another guarded table holds, soft-deleted
                    -- or not, nor one a hard delete tombstoned: the target rule clears a tombstoned id
                    -- that no identity holds, so any row on it would have its entries cleared. Its own
                    -- table is covered by its unique index on public_id.
                    IF EXISTS (
                        SELECT 1 FROM heimdall.person x
                        WHERE TG_TABLE_NAME <> 'person' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.google_user x
                        WHERE TG_TABLE_NAME <> 'google_user' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.scope x
                        WHERE TG_TABLE_NAME <> 'scope' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.application x
                        WHERE TG_TABLE_NAME <> 'application' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.application_credential x
                        WHERE TG_TABLE_NAME <> 'application_credential' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.scope_permission x
                        WHERE TG_TABLE_NAME <> 'scope_permission' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.role x
                        WHERE TG_TABLE_NAME <> 'role' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.pending_registration x
                        WHERE TG_TABLE_NAME <> 'pending_registration' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.scope_owner_invitation x
                        WHERE TG_TABLE_NAME <> 'scope_owner_invitation' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.erased_subject e WHERE e.public_id = NEW.public_id
                    ) THEN
                        RAISE EXCEPTION 'heimdall.% public_id is already used by another entity', TG_TABLE_NAME
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    -- A person or Google User may not take an id the audit trail names either, or a
                    -- hard-deleted entity's entries would become clearable through it. Only these two
                    -- tables: the target rule reads only them, and the tombstones, to tell a person's
                    -- id. A genuine new identity's own entries are written after its row.
                    IF TG_TABLE_NAME IN ('person', 'google_user') AND EXISTS (
                        SELECT 1 FROM heimdall.audit_log a WHERE a.target_id = NEW.public_id
                    ) THEN
                        RAISE EXCEPTION 'heimdall.% public_id is named by an audit entry', TG_TABLE_NAME
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER scope_owner_invitation_public_id_is_immutable
                BEFORE UPDATE ON heimdall.scope_owner_invitation
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_immutable();

                CREATE TRIGGER scope_owner_invitation_public_id_is_unused
                BEFORE INSERT ON heimdall.scope_owner_invitation
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_unused();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS scope_owner_invitation_public_id_is_unused ON heimdall.scope_owner_invitation;
                DROP TRIGGER IF EXISTS scope_owner_invitation_public_id_is_immutable ON heimdall.scope_owner_invitation;
                """);

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION heimdall.public_id_is_unused()
                RETURNS TRIGGER AS $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM heimdall.person x
                        WHERE TG_TABLE_NAME <> 'person' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.google_user x
                        WHERE TG_TABLE_NAME <> 'google_user' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.scope x
                        WHERE TG_TABLE_NAME <> 'scope' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.application x
                        WHERE TG_TABLE_NAME <> 'application' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.application_credential x
                        WHERE TG_TABLE_NAME <> 'application_credential' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.scope_permission x
                        WHERE TG_TABLE_NAME <> 'scope_permission' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.role x
                        WHERE TG_TABLE_NAME <> 'role' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.pending_registration x
                        WHERE TG_TABLE_NAME <> 'pending_registration' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.erased_subject e WHERE e.public_id = NEW.public_id
                    ) THEN
                        RAISE EXCEPTION 'heimdall.% public_id is already used by another entity', TG_TABLE_NAME
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    IF TG_TABLE_NAME IN ('person', 'google_user') AND EXISTS (
                        SELECT 1 FROM heimdall.audit_log a WHERE a.target_id = NEW.public_id
                    ) THEN
                        RAISE EXCEPTION 'heimdall.% public_id is named by an audit entry', TG_TABLE_NAME
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.DropTable(
                name: "scope_owner_invitation",
                schema: "heimdall");
        }
    }
}
