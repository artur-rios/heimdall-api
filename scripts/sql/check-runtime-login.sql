-- Checks, connected as the runtime login, that it cannot alter, disable or remove the audit
-- triggers or their functions, delete or truncate the trail, create anything, or alter any table
-- (NFR-29, Threat Model TH-18). Run it after new-deployment.sql or separate-owner.sql, as the login
-- the API connects with:
--
--     psql "host=<host> dbname=heimdall user=heimdall_svc" -X -f scripts/sql/check-runtime-login.sql
--
-- Each statement must be refused for want of privilege. One that succeeds, or is stopped by
-- anything else — a trigger included, which means the privilege is still there — is listed, and the
-- script fails. Run as the owner or a superuser, it lists every statement: that is the wrong
-- session, not a broken setup.
--
-- It changes nothing, whoever runs it: each statement runs in a subtransaction that is always rolled
-- back, which also releases at once any lock the statement took, and the whole is rolled back again
-- at the end.

\set ON_ERROR_STOP on

BEGIN;

SET LOCAL lock_timeout = '2s';

DO $$
DECLARE
    statements text[] := ARRAY[
        'ALTER TABLE heimdall.audit_log DISABLE TRIGGER USER',
        'ALTER TABLE heimdall.audit_log DISABLE TRIGGER ALL',
        'DROP TRIGGER audit_log_no_delete ON heimdall.audit_log',
        'DROP TRIGGER audit_log_no_truncate ON heimdall.audit_log',
        'DROP TRIGGER audit_log_attribution_clear_only ON heimdall.audit_log',
        'CREATE OR REPLACE FUNCTION heimdall.audit_log_attribution_clear_only() RETURNS trigger '
            'AS $f$ BEGIN RETURN NEW; END; $f$ LANGUAGE plpgsql',
        'CREATE OR REPLACE FUNCTION heimdall.audit_log_is_append_only() RETURNS trigger '
            'AS $f$ BEGIN RETURN NULL; END; $f$ LANGUAGE plpgsql',
        'DROP FUNCTION heimdall.audit_log_is_append_only() CASCADE',
        'CREATE TRIGGER planted BEFORE INSERT ON heimdall.audit_log '
            'FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_immutable()',
        'TRUNCATE heimdall.audit_log',
        'DELETE FROM heimdall.audit_log',
        'SET session_replication_role = replica',
        'CREATE TABLE heimdall.planted (id integer)',
        'CREATE TABLE public.planted (id integer)',
        'CREATE SCHEMA planted'
    ];
    statement text;
    holes text[] := '{}';
BEGIN
    -- And an ALTER TABLE on every table there is, so a table added later is checked too.
    statements := statements || ARRAY(
        SELECT format('ALTER TABLE %I.%I ADD COLUMN planted integer', schemaname, tablename)
        FROM pg_tables
        WHERE schemaname IN ('heimdall', 'public')
        ORDER BY schemaname, tablename);

    FOREACH statement IN ARRAY statements LOOP
        BEGIN
            EXECUTE statement;

            -- Not refused. Raised so the subtransaction rolls back what the statement did.
            RAISE EXCEPTION USING ERRCODE = 'HMD01';
        EXCEPTION
            WHEN insufficient_privilege THEN
                NULL;
            WHEN SQLSTATE 'HMD01' THEN
                holes := holes || format('%s: not refused', statement);
            WHEN OTHERS THEN
                holes := holes || format('%s: %s', statement, SQLERRM);
        END;
    END LOOP;

    IF cardinality(holes) > 0 THEN
        RAISE EXCEPTION E'% may still change the schema or the trail:\n%',
            current_user, array_to_string(holes, E'\n');
    END IF;

    RAISE NOTICE '% was refused all % statements', current_user, cardinality(statements);
END
$$;

ROLLBACK;
