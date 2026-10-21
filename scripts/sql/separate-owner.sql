-- Moves an existing deployment to the two-role setup (NFR-29, Threat Model TH-18). The deployment
-- guide used to create the database owned by the API's own login, which then applied the
-- migrations and so owned every table, sequence and trigger function. A table's owner may disable,
-- drop or replace its triggers without being a superuser, so the audit trail's triggers held
-- against everybody but the one credential the API runs with.
--
-- This creates the owner role, gives it the database and everything the runtime login owns in it,
-- and leaves the login exactly the privileges runtime-grants.sql gives. Run as a superuser, with the
-- three names as psql variables:
--
--     sudo -u postgres psql -X -v owner=heimdall_owner -v runtime=heimdall_svc \
--         -v database=heimdall -f scripts/sql/separate-owner.sql
--
-- then set the owner's password (\password heimdall_owner) — this script holds none — and deploy
-- with HEIMDALL_MIGRATIONS_CONNECTIONSTRING naming the owner. Until then the API keeps running as
-- before, but a container start that migrates as the runtime login fails: it no longer owns what
-- the migrations change. The operations guide, "Separating the migration owner from the runtime
-- login", has the whole order, the checks and the way back.
--
-- One transaction: the API sees the old ownership or the new one with its grants, never a mix. It
-- takes a lock on every table it changes and holds them until COMMIT, so requests wait for that
-- moment; lock_timeout gives up after five seconds rather than queue behind a long transaction, and
-- the whole of it rolls back. Run it again at a quieter moment if it does. Running it again after
-- it succeeded is harmless too.

\set ON_ERROR_STOP on

\if :{?owner}
\else
DO $$ BEGIN RAISE EXCEPTION 'set the owner role: psql -v owner=<name>'; END $$;
\endif
\if :{?runtime}
\else
DO $$ BEGIN RAISE EXCEPTION 'set the runtime login: psql -v runtime=<name>'; END $$;
\endif
\if :{?database}
\else
DO $$ BEGIN RAISE EXCEPTION 'set the database: psql -v database=<name>'; END $$;
\endif

-- REASSIGN OWNED works on the connected database alone, so this has to be the deployment's.
\connect :"database"

BEGIN;

SET LOCAL lock_timeout = '5s';
SET LOCAL heimdall_roles.owner = :'owner';
SET LOCAL heimdall_roles.runtime = :'runtime';

DO $$
DECLARE
    owner_name text := current_setting('heimdall_roles.owner');
    runtime_name text := current_setting('heimdall_roles.runtime');
    elsewhere text;
BEGIN
    IF owner_name = runtime_name THEN
        RAISE EXCEPTION 'the owner and the runtime login must be two roles, and both are %', owner_name;
    END IF;

    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = runtime_name) THEN
        RAISE EXCEPTION 'the runtime login % does not exist', runtime_name;
    END IF;

    -- REASSIGN OWNED also moves the databases and tablespaces the login owns anywhere on the
    -- cluster, which is not this script's business: an instance shared with other services or
    -- environments may hold one. Those are moved by hand, or this is not the login to separate.
    SELECT string_agg(name, ', ' ORDER BY name) INTO elsewhere
    FROM (
        SELECT 'database ' || datname AS name FROM pg_database
        WHERE datdba = runtime_name::regrole AND datname <> current_database()
        UNION ALL
        SELECT 'tablespace ' || spcname FROM pg_tablespace WHERE spcowner = runtime_name::regrole
    ) AS shared;

    IF elsewhere IS NOT NULL THEN
        RAISE EXCEPTION 'the runtime login % also owns %, which REASSIGN OWNED would move to % as well; '
            'change their owner first', runtime_name, elsewhere, owner_name;
    END IF;
END
$$;

SELECT format('CREATE ROLE %I LOGIN', :'owner')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = :'owner') \gexec

-- The database, then everything the login owns in it: the heimdall schema, the tables with their
-- sequences and indexes, the trigger functions and the migration history in public. A trigger has
-- no owner of its own; it belongs to its table.
SELECT format('ALTER DATABASE %I OWNER TO %I', current_database(), :'owner') \gexec
REASSIGN OWNED BY :"runtime" TO :"owner";

\ir runtime-grants.sql

COMMIT;
