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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "erased_subject",
                schema: "heimdall");
        }
    }
}
