using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds <c>erased_subject</c>: the public id of a person or Google User that was hard-deleted,
    ///     kept only until the audit trail no longer names it (NFR-21, #195).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>audit_log.target_id</c> is a bare id with no foreign key, so it outlives the row it
    ///         named, and the trigger clears it only once it can tell the target was a person. After a
    ///         hard delete nothing could tell it so, and the entries about that person kept an id that
    ///         a backup, an export or a log line could join back to them. Each hard delete now writes
    ///         the id here first; <c>AllowClearingErasedSubjectTarget</c> lets the trigger accept the
    ///         clearing on that ground, and the retention pass removes the row once nothing names it.
    ///     </para>
    ///     <para>
    ///         Keyed by the id itself, so a retried hard delete cannot write it twice. <c>kind</c> is
    ///         <c>ErasedSubjectKinds</c> as an integer, like every other enum column here. No foreign
    ///         key: the row it names is about to go. The index on <c>created_at</c> serves the pass's
    ///         cleanup. A new table holding processing records joins the record of processing (Data
    ///         Protection Document §5.1) and the Data Retention Schedule §7.
    ///     </para>
    ///     <para>
    ///         <b>Only a person or Google User that exists may be recorded (#195, R1).</b> A row here
    ///         is what lets the audit trail's trigger clear a target that resolves to no row, so a
    ///         table anyone could write to would have handed anyone with the API's database
    ///         credentials two DML statements — tombstone a live scope's id, then clear every target
    ///         naming it — and the pass would have removed the planted row a day later, leaving no
    ///         trace. Before the tombstones, that clearing was refused outright. A row-level
    ///         <c>BEFORE INSERT OR UPDATE</c> trigger, <c>erased_subject_names_a_person</c>, therefore
    ///         refuses any row whose <c>public_id</c> is not, at that moment, the <c>public_id</c> of
    ///         a <c>person</c> or <c>google_user</c> row. Every legitimate write passes, because a hard
    ///         delete writes its tombstones before deleting; so does the upsert's <c>DO UPDATE</c> on
    ///         a retry, because a retry only happens while the row survives. Checking UPDATE too stops
    ///         a legitimate tombstone being rewritten to another id afterwards.
    ///     </para>
    /// </remarks>
    public partial class AddErasedSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "erased_subject",
                schema: "heimdall",
                columns: table => new
                {
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erased_subject", x => x.public_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_erased_subject_created_at",
                schema: "heimdall",
                table: "erased_subject",
                column: "created_at");

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION heimdall.erased_subject_names_a_person()
                RETURNS TRIGGER AS $$
                BEGIN
                    -- A tombstone is written just before its person or Google User is deleted, so the
                    -- row it names always exists at that moment. Anything else is not a tombstone,
                    -- and would let the audit trail's trigger clear a target that is not a person's.
                    IF NOT EXISTS (
                        SELECT 1 FROM heimdall.person p WHERE p.public_id = NEW.public_id
                        UNION ALL
                        SELECT 1 FROM heimdall.google_user g WHERE g.public_id = NEW.public_id
                    ) THEN
                        RAISE EXCEPTION
                            'heimdall.erased_subject may only record a person or Google User that exists'
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER erased_subject_names_a_person
                BEFORE INSERT OR UPDATE ON heimdall.erased_subject
                FOR EACH ROW EXECUTE FUNCTION heimdall.erased_subject_names_a_person();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS erased_subject_names_a_person ON heimdall.erased_subject;");
            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS heimdall.erased_subject_names_a_person();");

            migrationBuilder.DropTable(
                name: "erased_subject",
                schema: "heimdall");
        }
    }
}
