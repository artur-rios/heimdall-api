-- A new deployment's database and its two roles (NFR-29, Threat Model TH-18): an owner, which owns
-- the database and everything the migrations create, and a runtime login, which the API connects as
-- and which owns nothing. The audit trail's triggers bind every role but the tables' owner and a
-- superuser, so they hold against the API's credentials only when those are the runtime login's.
--
-- Run as a superuser, connected to any database, with the three names as psql variables:
--
--     sudo -u postgres psql -X -v owner=heimdall_owner -v runtime=heimdall_svc \
--         -v database=heimdall -f scripts/sql/new-deployment.sql
--
-- then, in this order:
--
--  1. Set both passwords: \password heimdall_owner and \password heimdall_svc. This script holds
--     none and creates both logins without one, so neither can sign in until then.
--  2. Deploy with DB_USER naming the runtime login and HEIMDALL_MIGRATIONS_CONNECTIONSTRING the
--     owner. The entrypoint migrates as the owner, and the default privileges set here grant the
--     runtime login every table the migrations create.
--  3. Run this script again. The first migration has created audit_log by then, and the second run
--     takes away the DELETE the default privileges gave the login on it.
--
-- Idempotent: what exists is left alone, and the grants are reset to exactly what
-- runtime-grants.sql gives. A database that exists and belongs to anybody but the owner is refused;
-- one that belongs to the runtime login is an existing deployment, which separate-owner.sql moves
-- over.

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

SET heimdall_roles.owner = :'owner';
SET heimdall_roles.runtime = :'runtime';
SET heimdall_roles.database = :'database';

DO $$
DECLARE
    owner_name text := current_setting('heimdall_roles.owner');
    runtime_name text := current_setting('heimdall_roles.runtime');
    database_name text := current_setting('heimdall_roles.database');
    database_owner text;
BEGIN
    IF owner_name = runtime_name THEN
        RAISE EXCEPTION 'the owner and the runtime login must be two roles, and both are %', owner_name;
    END IF;

    SELECT pg_get_userbyid(datdba) INTO database_owner FROM pg_database WHERE datname = database_name;

    IF database_owner = runtime_name THEN
        RAISE EXCEPTION 'database % belongs to the runtime login %: it is an existing deployment, '
            'which separate-owner.sql moves over', database_name, runtime_name;
    END IF;

    IF database_owner <> owner_name THEN
        RAISE EXCEPTION 'database % already exists and belongs to %, not to the owner %',
            database_name, database_owner, owner_name;
    END IF;
END
$$;

SELECT format('CREATE ROLE %I LOGIN', :'owner')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = :'owner') \gexec
SELECT format('CREATE ROLE %I LOGIN', :'runtime')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = :'runtime') \gexec

-- CREATE DATABASE cannot run inside a transaction, so it comes before the one below.
SELECT format('CREATE DATABASE %I OWNER %I', :'database', :'owner')
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = :'database') \gexec

\connect :"database"

BEGIN;

-- Created here rather than left to the first migration, so the default privileges below have a
-- schema to be set in before any table exists. The migration's own CREATE SCHEMA IF NOT EXISTS then
-- finds it.
CREATE SCHEMA IF NOT EXISTS heimdall AUTHORIZATION :"owner";

\ir runtime-grants.sql

COMMIT;
