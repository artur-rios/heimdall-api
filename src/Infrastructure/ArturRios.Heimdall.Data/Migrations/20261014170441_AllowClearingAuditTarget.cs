using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Widens the audit log's append-only rule by exactly one more permitted change: a target
    ///     naming a person or Google User may be cleared once it is due (NFR-21, #184).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         `AllowClearingAuditActor` let the actor attribution be forgotten and left `target_id`
    ///         immutable. That was not enough to make an entry anonymous. For a command somebody runs
    ///         on their own account the target is their own `PublicId`, and for an administrator's
    ///         action on a person it is that person, so the entry kept naming somebody for as long as
    ///         their `person` or `google_user` row stayed identifiable — whatever happened to the
    ///         actor. UC-48's filter by `targetId` made that a one-query lookup.
    ///     </para>
    ///     <para>
    ///         This replaces `heimdall.audit_log_actor_clear_only()` with
    ///         `heimdall.audit_log_attribution_clear_only()`, still a row-level BEFORE UPDATE
    ///         trigger. The actor rule is unchanged. The one addition: `target_id` may go from a
    ///         value to NULL — never to another value — and only when the old value is the `public_id`
    ///         of a `person` or `google_user` row and the clearing is due on the actor's own two
    ///         grounds: that row has been anonymised, or the entry is past the thirty-day floor.
    ///         A target naming anything else (a scope, an application, a credential, a permission)
    ///         is not personal data and stays immutable, as does one that resolves to nobody.
    ///     </para>
    ///     <para>
    ///         Each of the two clearable parts may now also be written back unchanged, so the pass
    ///         can clear a due target while leaving an undue actor in place, and the reverse. The
    ///         row's `id`, unguarded before, joins the immutable columns: once an unchanged actor is
    ///         an accepted write, leaving `id` out would have let any row be renumbered, a change
    ///         the old rule refused on every attributed row. DELETE and TRUNCATE stay refused
    ///         outright and their triggers are not touched.
    ///     </para>
    /// </remarks>
    public partial class AllowClearingAuditTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS audit_log_actor_clear_only ON heimdall.audit_log;");
            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS heimdall.audit_log_actor_clear_only();");

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION heimdall.audit_log_attribution_clear_only()
                RETURNS TRIGGER AS $$
                DECLARE
                    actor_erased boolean;
                    target_is_person boolean;
                    target_erased boolean;
                BEGIN
                    -- What happened is immutable. Checked first, so an attempt to rewrite it is
                    -- refused with that reason rather than a later one, and so created_at cannot be
                    -- moved to make a clearing look due.
                    IF NEW.id IS DISTINCT FROM OLD.id
                       OR NEW.public_id IS DISTINCT FROM OLD.public_id
                       OR NEW.action IS DISTINCT FROM OLD.action
                       OR NEW.succeeded IS DISTINCT FROM OLD.succeeded
                       OR NEW.failure_reason IS DISTINCT FROM OLD.failure_reason
                       OR NEW.created_at IS DISTINCT FROM OLD.created_at
                    THEN
                        RAISE EXCEPTION
                            'heimdall.audit_log is append-only: only the actor attribution and a person target may be cleared'
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    -- The actor: unchanged, or cleared once due. Never reassigned, because a trail
                    -- that says somebody else acted is worse than one that does not say who did.
                    IF NEW.actor_person_id IS DISTINCT FROM OLD.actor_person_id
                       OR NEW.actor_role IS DISTINCT FROM OLD.actor_role
                    THEN
                        IF NEW.actor_person_id IS NOT NULL OR NEW.actor_role IS NOT NULL THEN
                            RAISE EXCEPTION
                                'heimdall.audit_log actor attribution may be cleared but not reassigned'
                                USING ERRCODE = 'restrict_violation';
                        END IF;

                        SELECT EXISTS (
                            SELECT 1 FROM heimdall.person p
                            WHERE p.public_id = OLD.actor_person_id AND p.anonymised_at IS NOT NULL
                            UNION ALL
                            SELECT 1 FROM heimdall.google_user g
                            WHERE g.public_id = OLD.actor_person_id AND g.anonymised_at IS NOT NULL
                        ) INTO actor_erased;

                        IF NOT (actor_erased OR OLD.created_at <= now() - interval '30 days') THEN
                            RAISE EXCEPTION
                                'heimdall.audit_log actor attribution is not yet due to be cleared'
                                USING ERRCODE = 'restrict_violation';
                        END IF;
                    END IF;

                    -- The target: unchanged, or cleared once due, and only when it names a person.
                    -- Anything else it can name is not personal data and stays as recorded.
                    IF NEW.target_id IS DISTINCT FROM OLD.target_id THEN
                        IF NEW.target_id IS NOT NULL AND OLD.target_id IS NULL THEN
                            RAISE EXCEPTION
                                'heimdall.audit_log target may be cleared but not set'
                                USING ERRCODE = 'restrict_violation';
                        END IF;

                        IF NEW.target_id IS NOT NULL THEN
                            RAISE EXCEPTION
                                'heimdall.audit_log target may be cleared but not reassigned'
                                USING ERRCODE = 'restrict_violation';
                        END IF;

                        SELECT EXISTS (
                            SELECT 1 FROM heimdall.person p WHERE p.public_id = OLD.target_id
                            UNION ALL
                            SELECT 1 FROM heimdall.google_user g WHERE g.public_id = OLD.target_id
                        ) INTO target_is_person;

                        IF NOT target_is_person THEN
                            RAISE EXCEPTION
                                'heimdall.audit_log target names no person and may not be cleared'
                                USING ERRCODE = 'restrict_violation';
                        END IF;

                        SELECT EXISTS (
                            SELECT 1 FROM heimdall.person p
                            WHERE p.public_id = OLD.target_id AND p.anonymised_at IS NOT NULL
                            UNION ALL
                            SELECT 1 FROM heimdall.google_user g
                            WHERE g.public_id = OLD.target_id AND g.anonymised_at IS NOT NULL
                        ) INTO target_erased;

                        IF NOT (target_erased OR OLD.created_at <= now() - interval '30 days') THEN
                            RAISE EXCEPTION
                                'heimdall.audit_log target is not yet due to be cleared'
                                USING ERRCODE = 'restrict_violation';
                        END IF;
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER audit_log_attribution_clear_only
                BEFORE UPDATE ON heimdall.audit_log
                FOR EACH ROW EXECUTE FUNCTION heimdall.audit_log_attribution_clear_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS audit_log_attribution_clear_only ON heimdall.audit_log;");
            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS heimdall.audit_log_attribution_clear_only();");

            // Back to AllowClearingAuditActor's rule, copied verbatim: the actor alone, target_id
            // immutable.
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

            migrationBuilder.Sql(
                """
                CREATE TRIGGER audit_log_actor_clear_only
                BEFORE UPDATE ON heimdall.audit_log
                FOR EACH ROW EXECUTE FUNCTION heimdall.audit_log_actor_clear_only();
                """);
        }
    }
}
