using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Makes <c>public_id</c> on <c>heimdall.person</c> and <c>heimdall.google_user</c> immutable,
    ///     and refuses a new person or Google User whose <c>public_id</c> another entity holds or an
    ///     audit entry names (#199, Threat Model TH-18 and §6.1).
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
    ///         This creates the two functions every guarded table shares, and binds them to the two
    ///         identity tables; <c>ProtectEntityPublicIds</c> binds them to the other six. A row-level
    ///         <c>BEFORE UPDATE</c> trigger, <c>*_public_id_is_immutable</c>, runs
    ///         <c>public_id_is_immutable()</c>, which refuses any change to <c>public_id</c>; every
    ///         other column stays writable, and an EF update that writes the same value back passes. A
    ///         row-level <c>BEFORE INSERT</c> trigger, <c>*_public_id_is_unused</c>, runs
    ///         <c>public_id_is_unused()</c>, which refuses a <c>public_id</c> that a row of any other
    ///         guarded table holds: <c>person</c>, <c>google_user</c>, <c>scope</c>,
    ///         <c>application</c>, <c>application_credential</c>, <c>scope_permission</c>,
    ///         <c>role</c> and <c>pending_registration</c>. A table's own unique index covers itself.
    ///     </para>
    ///     <para>
    ///         <b>An identity may not take an id the audit trail names, either.</b> A row that is gone
    ///         holds no id, so after an ordinary hard delete of a scope, an application or anything
    ///         else, a person inserted with its id and marked anonymised would have made every entry
    ///         naming it clearable. <c>public_id_is_unused()</c> therefore also refuses, for
    ///         <c>person</c> and <c>google_user</c> only, an id that <c>audit_log.target_id</c> names.
    ///         Only those two tables need it: the target rule reads only them, and the tombstones, to
    ///         decide that a target is a person's, so no other table taking such an id changes what
    ///         may be cleared. It keeps nothing new, since the trail already holds the id, and it is
    ///         one probe of <c>ix_audit_log_target_id</c>. A genuine new identity always passes: its id
    ///         is a fresh <c>Guid.NewGuid()</c>, and its own entries are written after its row.
    ///     </para>
    ///     <para>
    ///         "Held" means by a row that is there now, soft-deleted or not; no registry of issued ids
    ///         is kept, because it would keep a removed person's id for good. Both triggers guard future
    ///         writes only; the migration does not look for a duplicate already stored. Down drops the
    ///         four triggers and both functions.
    ///     </para>
    /// </remarks>
    public partial class ProtectIdentityPublicIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION heimdall.public_id_is_immutable()
                RETURNS TRIGGER AS $$
                BEGIN
                    -- An id names one entity for as long as its row exists. The audit trail's
                    -- trigger and the tombstone guard rely on that, and so does public_id_is_unused(),
                    -- which can only see who holds an id at the moment it runs.
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
                CREATE OR REPLACE FUNCTION heimdall.public_id_is_unused()
                RETURNS TRIGGER AS $$
                BEGIN
                    -- A new row may not take an id a row of another guarded table holds, soft-deleted
                    -- or not. Its own table is covered by its unique index on public_id.
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
                CREATE TRIGGER person_public_id_is_immutable
                BEFORE UPDATE ON heimdall.person
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_immutable();

                CREATE TRIGGER google_user_public_id_is_immutable
                BEFORE UPDATE ON heimdall.google_user
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_immutable();

                CREATE TRIGGER person_public_id_is_unused
                BEFORE INSERT ON heimdall.person
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_unused();

                CREATE TRIGGER google_user_public_id_is_unused
                BEFORE INSERT ON heimdall.google_user
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_unused();
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
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS heimdall.public_id_is_unused();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS heimdall.public_id_is_immutable();");
        }
    }
}
