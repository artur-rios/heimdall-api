using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Enforces the audit trail's append-only rule in the database (NFR-09, Threat Model TH-18).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>AuditLog</c> has said "append-only: never updated or logically deleted after
    ///         creation" since it was written, and until now that was a sentence rather than a rule.
    ///         Nothing prevented an <c>UPDATE</c> or a <c>DELETE</c>, and the credentials the API
    ///         itself connects with were sufficient to issue one — so the trail was evidence against
    ///         an ordinary caller and no evidence at all against anyone who reached the database.
    ///     </para>
    ///     <para>
    ///         A trigger rather than a permission grant, because a grant depends on the deployment
    ///         connecting as a role that lacks those rights, and this repository's own compose file,
    ///         the functional suite and most development setups all connect as the owner. A trigger
    ///         holds regardless of who is connected, including the owner, and travels with the schema
    ///         instead of with the deployment's configuration.
    ///     </para>
    ///     <para>
    ///         Nothing in the application writes an audit row twice: <c>AuditLogWriter</c> only ever
    ///         calls <c>CreateAsync</c>, and the table carries no foreign key — <c>ActorPersonId</c>
    ///         is a bare <c>PublicId</c> by design — so no cascade from a hard deletion reaches it.
    ///         Confirmed by search across <c>src/</c> and <c>tests/</c> before this was added.
    ///     </para>
    ///     <para>
    ///         What this does not defend against is somebody who can also run DDL: a superuser can
    ///         drop the trigger and then rewrite history. That is a smaller hole than the one it
    ///         closes — it leaves a schema change behind, where an <c>UPDATE</c> left nothing — but it
    ///         is not zero, and TH-18 says so rather than claiming the trail is now tamper-proof.
    ///     </para>
    /// </remarks>
    public partial class MakeAuditLogAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION heimdall.audit_log_is_append_only()
                RETURNS TRIGGER AS $$
                BEGIN
                    RAISE EXCEPTION
                        'heimdall.audit_log is append-only: % is not permitted', TG_OP
                        USING ERRCODE = 'restrict_violation';
                END;
                $$ LANGUAGE plpgsql;
                """);

            // FOR EACH STATEMENT, not FOR EACH ROW: the statement is refused whether it would have
            // matched one row, many, or none. A row-level trigger would let `DELETE FROM audit_log
            // WHERE false` succeed, which is harmless in itself but makes the rule depend on how
            // much the statement happened to match.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER audit_log_no_update
                BEFORE UPDATE ON heimdall.audit_log
                FOR EACH STATEMENT EXECUTE FUNCTION heimdall.audit_log_is_append_only();
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER audit_log_no_delete
                BEFORE DELETE ON heimdall.audit_log
                FOR EACH STATEMENT EXECUTE FUNCTION heimdall.audit_log_is_append_only();
                """);

            // TRUNCATE bypasses BEFORE DELETE entirely — it is DDL-ish rather than a row operation —
            // so it needs its own trigger, or the rule has a one-word way around it.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER audit_log_no_truncate
                BEFORE TRUNCATE ON heimdall.audit_log
                FOR EACH STATEMENT EXECUTE FUNCTION heimdall.audit_log_is_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_log_no_truncate ON heimdall.audit_log;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_log_no_delete ON heimdall.audit_log;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_log_no_update ON heimdall.audit_log;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS heimdall.audit_log_is_append_only();");
        }
    }
}
