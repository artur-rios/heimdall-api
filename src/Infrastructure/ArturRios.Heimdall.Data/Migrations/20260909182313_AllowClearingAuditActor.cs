using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Narrows the audit log's append-only rule by exactly one permitted change: the actor
    ///     attribution may be cleared once it is due (NFR-21).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         `MakeAuditLogAppendOnly` refused every UPDATE, DELETE and TRUNCATE, which closed
    ///         Threat Model TH-18 and also made erasure impossible: `actor_person_id` holds a
    ///         person's `PublicId`, so an indefinitely retained, unalterable row naming an erased
    ///         person is personal data processed with no remaining basis.
    ///     </para>
    ///     <para>
    ///         Rather than dropping the guarantee, this narrows it. DELETE and TRUNCATE stay refused
    ///         outright. UPDATE becomes row-level and is permitted only when every column except
    ///         `actor_person_id` and `actor_role` is unchanged, the attribution is being set to
    ///         NULL rather than to somebody else, and the clearing is due. The resulting rule is
    ///         precise: <b>an action can never be denied, and who took it can never be reassigned —
    ///         only forgotten, on schedule or on erasure.</b>
    ///     </para>
    ///     <para>
    ///         "Due" means the entry is older than a thirty-day floor, or the identity it names has
    ///         been anonymised. The floor is an anti-tamper backstop rather than a retention policy:
    ///         the retention period is configuration and is far longer, and the floor exists so that
    ///         somebody who has just acted cannot immediately erase their own attribution. The
    ///         erasure branch carries no age condition, because a subject-requested erasure completes
    ///         thirty days after the request and their most recent entries must go with them.
    ///     </para>
    /// </remarks>
    public partial class AllowClearingAuditActor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION heimdall.audit_log_actor_clear_only()
                RETURNS TRIGGER AS $$
                DECLARE
                    actor_erased boolean;
                BEGIN
                    -- Everything but the attribution is immutable. Checked first, so an attempt to
                    -- rewrite what happened is refused with that reason rather than a later one.
                    IF NEW.public_id IS DISTINCT FROM OLD.public_id
                       OR NEW.action IS DISTINCT FROM OLD.action
                       OR NEW.target_id IS DISTINCT FROM OLD.target_id
                       OR NEW.succeeded IS DISTINCT FROM OLD.succeeded
                       OR NEW.failure_reason IS DISTINCT FROM OLD.failure_reason
                       OR NEW.created_at IS DISTINCT FROM OLD.created_at
                    THEN
                        RAISE EXCEPTION
                            'heimdall.audit_log is append-only: only the actor attribution may be cleared'
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    -- Cleared, never reassigned. Without this the trail could be made to say that
                    -- somebody else took the action, which is worse than it not saying who did.
                    IF NEW.actor_person_id IS NOT NULL OR NEW.actor_role IS NOT NULL THEN
                        RAISE EXCEPTION
                            'heimdall.audit_log actor attribution may be cleared but not reassigned'
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    -- Already clear: a no-op write, permitted so a re-run is not an error.
                    IF OLD.actor_person_id IS NULL AND OLD.actor_role IS NULL THEN
                        RETURN NEW;
                    END IF;

                    SELECT EXISTS (
                        SELECT 1 FROM heimdall.person p
                        WHERE p.public_id = OLD.actor_person_id AND p.anonymised_at IS NOT NULL
                        UNION ALL
                        SELECT 1 FROM heimdall.google_user g
                        WHERE g.public_id = OLD.actor_person_id AND g.anonymised_at IS NOT NULL
                    ) INTO actor_erased;

                    IF actor_erased OR OLD.created_at <= now() - interval '30 days' THEN
                        RETURN NEW;
                    END IF;

                    RAISE EXCEPTION
                        'heimdall.audit_log actor attribution is not yet due to be cleared'
                        USING ERRCODE = 'restrict_violation';
                END;
                $$ LANGUAGE plpgsql;
                """);

            // The statement-level UPDATE trigger goes: the rule is now about which row-level change
            // is being made, which a statement-level trigger cannot see.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_log_no_update ON heimdall.audit_log;");

            migrationBuilder.Sql(
                """
                CREATE TRIGGER audit_log_actor_clear_only
                BEFORE UPDATE ON heimdall.audit_log
                FOR EACH ROW EXECUTE FUNCTION heimdall.audit_log_actor_clear_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS audit_log_actor_clear_only ON heimdall.audit_log;");
            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS heimdall.audit_log_actor_clear_only();");

            // Back to refusing every UPDATE.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER audit_log_no_update
                BEFORE UPDATE ON heimdall.audit_log
                FOR EACH STATEMENT EXECUTE FUNCTION heimdall.audit_log_is_append_only();
                """);
        }
    }
}
