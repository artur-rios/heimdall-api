using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Makes <c>public_id</c> on <c>heimdall.person</c> and <c>heimdall.google_user</c> immutable,
    ///     and refuses a new person or Google User whose <c>public_id</c> another entity already holds
    ///     (#199, Threat Model TH-18 and §6.1).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two database rules read an identity row's <c>public_id</c> as proof that an id is a
    ///         person's: <c>audit_log_attribution_clear_only()</c> clears a target that names a person
    ///         or Google User (#184), and <c>erased_subject_names_a_person</c> accepts a tombstone only
    ///         for one that exists (#195). Nothing stopped a row being given a scope's, an
    ///         application's or a credential's id, by rewriting it or by inserting a new one, and
    ///         either way the targets naming that entity became clearable although they never named a
    ///         person.
    ///     </para>
    ///     <para>
    ///         A row-level <c>BEFORE UPDATE</c> trigger on each table, <c>*_public_id_is_immutable</c>,
    ///         refuses any change to <c>public_id</c>; every other column stays writable, and an EF
    ///         update that writes the same value back passes. A row-level <c>BEFORE INSERT</c> trigger,
    ///         <c>*_public_id_is_unused</c>, refuses a <c>public_id</c> that is the <c>public_id</c> of a
    ///         row in <c>scope</c>, <c>application</c>, <c>application_credential</c>,
    ///         <c>scope_permission</c>, <c>role</c>, <c>pending_registration</c> or the other identity
    ///         table. Its own table is covered by its unique index. The application never sets either
    ///         id: every one is a fresh <c>Guid.NewGuid()</c>, so no legitimate write is refused.
    ///     </para>
    ///     <para>
    ///         "Held" means by a row that is there now, soft-deleted or not. A hard-deleted entity's id
    ///         is not remembered, by design: a registry of issued ids would keep a removed person's id
    ///         for good. Both triggers guard future writes only; the migration does not look for a
    ///         duplicate already stored. Down drops the triggers and their two functions.
    ///     </para>
    /// </remarks>
    public partial class ProtectIdentityPublicIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION heimdall.identity_public_id_is_immutable()
                RETURNS TRIGGER AS $$
                BEGIN
                    -- The audit trail's trigger and the tombstone guard take an identity row's
                    -- public_id as proof the id is a person's, so it may never become another id.
                    IF NEW.public_id IS DISTINCT FROM OLD.public_id THEN
                        RAISE EXCEPTION 'heimdall.% public_id is immutable', TG_TABLE_NAME
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION heimdall.identity_public_id_is_unused()
                RETURNS TRIGGER AS $$
                BEGIN
                    -- A new identity may not take an id another entity holds, soft-deleted or not.
                    -- Its own table is covered by its unique index on public_id.
                    IF EXISTS (
                        SELECT 1 FROM heimdall.scope x WHERE x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.application x WHERE x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.application_credential x WHERE x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.scope_permission x WHERE x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.role x WHERE x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.pending_registration x WHERE x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.google_user x
                        WHERE TG_TABLE_NAME = 'person' AND x.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.person x
                        WHERE TG_TABLE_NAME = 'google_user' AND x.public_id = NEW.public_id
                    ) THEN
                        RAISE EXCEPTION 'heimdall.% public_id is already used by another entity', TG_TABLE_NAME
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER person_public_id_is_immutable
                BEFORE UPDATE ON heimdall.person
                FOR EACH ROW EXECUTE FUNCTION heimdall.identity_public_id_is_immutable();

                CREATE TRIGGER google_user_public_id_is_immutable
                BEFORE UPDATE ON heimdall.google_user
                FOR EACH ROW EXECUTE FUNCTION heimdall.identity_public_id_is_immutable();

                CREATE TRIGGER person_public_id_is_unused
                BEFORE INSERT ON heimdall.person
                FOR EACH ROW EXECUTE FUNCTION heimdall.identity_public_id_is_unused();

                CREATE TRIGGER google_user_public_id_is_unused
                BEFORE INSERT ON heimdall.google_user
                FOR EACH ROW EXECUTE FUNCTION heimdall.identity_public_id_is_unused();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS google_user_public_id_is_unused ON heimdall.google_user;
                DROP TRIGGER IF EXISTS person_public_id_is_unused ON heimdall.person;
                DROP TRIGGER IF EXISTS google_user_public_id_is_immutable ON heimdall.google_user;
                DROP TRIGGER IF EXISTS person_public_id_is_immutable ON heimdall.person;
                """);
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS heimdall.identity_public_id_is_unused();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS heimdall.identity_public_id_is_immutable();");
        }
    }
}
