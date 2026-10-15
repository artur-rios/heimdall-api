using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Widens the audit log's target rule by exactly one more permitted change: a target whose id
    ///     was tombstoned by a hard delete, and that no person or Google User row has any more, may be
    ///     cleared at any age (NFR-21, #195).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>AllowClearingAuditTarget</c> let a target naming a person be cleared once due, but
    ///         only while the person's row was there to say it was a person's. A hard delete (UC-10,
    ///         UC-29, UC-05's members) took that row away, and the trigger then refused the clearing
    ///         for good as "names no person": the entries about the removed person, and the hard
    ///         delete's own entry, kept naming them by an id that anything holding a copy of the old
    ///         row could still resolve.
    ///     </para>
    ///     <para>
    ///         Each hard delete now records the id in <c>heimdall.erased_subject</c> first
    ///         (<c>AddErasedSubject</c>). This replaces the body of
    ///         <c>heimdall.audit_log_attribution_clear_only()</c>, still bound to the same row-level
    ///         BEFORE UPDATE trigger, so that a clearing from a value to NULL is also accepted when the
    ///         old value is in <c>erased_subject</c> and no <c>person</c> or <c>google_user</c> row has
    ///         it. The tombstone says the id was a person's; the missing row says the delete really
    ///         happened, so a tombstone left by a delete that failed permits nothing. Such a target is
    ///         due at once, as an anonymised person's is: the person is gone, which is the outcome an
    ///         anonymisation only approximates.
    ///     </para>
    ///     <para>
    ///         Nothing else changes: the immutable columns, the actor rule, "cleared but not set", the
    ///         refusal of reassignment, and the refusal to clear an id that resolves to nobody and was
    ///         never tombstoned — a hard-deleted scope's, or a person's removed before this migration,
    ///         whose id nothing recorded. Down restores the function exactly as
    ///         <c>AllowClearingAuditTarget</c> installed it.
    ///     </para>
    /// </remarks>
    public partial class AllowClearingErasedSubjectTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CREATE OR REPLACE keeps the trigger bound to the function; only the body changes.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION heimdall.audit_log_attribution_clear_only()
                RETURNS TRIGGER AS $$
                DECLARE
                    actor_erased boolean;
                    target_is_person boolean;
                    target_erased boolean;
                    target_hard_deleted boolean;
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

                    -- The target: unchanged, or cleared once due, and only when it names a person,
                    -- on record or tombstoned. Anything else it can name is not personal data and
                    -- stays as recorded.
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
                            -- No row has the id. It may still have been a person's: a hard delete
                            -- tombstones the id before the row goes (#195). Tombstoned and gone means
                            -- the delete happened, so the target is due at once, at any age.
                            -- Anything else that resolves to nobody stays as recorded.
                            SELECT EXISTS (
                                SELECT 1 FROM heimdall.erased_subject e WHERE e.public_id = OLD.target_id
                            ) INTO target_hard_deleted;

                            IF NOT target_hard_deleted THEN
                                RAISE EXCEPTION
                                    'heimdall.audit_log target names no person and may not be cleared'
                                    USING ERRCODE = 'restrict_violation';
                            END IF;
                        ELSE
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
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to AllowClearingAuditTarget's rule, copied verbatim: a target that resolves to no
            // row is never cleared.
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
        }
    }
}
