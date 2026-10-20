-- The runtime login's privileges in the connected database: what the API does at runtime and
-- nothing more (NFR-29, Threat Model TH-18). Not run on its own: new-deployment.sql and
-- separate-owner.sql include it, inside their transaction, with the psql variables owner and
-- runtime set.
--
-- Everything the login held is revoked first and then granted again, so the result is the same
-- whatever it held before, and running the including script again is how a deployment is brought
-- back to exactly these grants.

SET LOCAL heimdall_roles.owner = :'owner';
SET LOCAL heimdall_roles.runtime = :'runtime';

-- Grants restrict nothing for a login that can act as the owner or past every check, so that is
-- refused before anything is granted.
DO $$
DECLARE
    owner_name text := current_setting('heimdall_roles.owner');
    runtime_name text := current_setting('heimdall_roles.runtime');
    runtime_login pg_roles%ROWTYPE;
    owned bigint;
BEGIN
    IF owner_name = runtime_name THEN
        RAISE EXCEPTION 'the owner and the runtime login must be two roles, and both are %', owner_name;
    END IF;

    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = owner_name) THEN
        RAISE EXCEPTION 'the owner role % does not exist', owner_name;
    END IF;

    SELECT * INTO runtime_login FROM pg_roles WHERE rolname = runtime_name;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'the runtime login % does not exist', runtime_name;
    END IF;

    IF runtime_login.rolsuper OR runtime_login.rolcreaterole THEN
        RAISE EXCEPTION 'the runtime login % is a superuser or may create roles, which no grant restricts; '
            'ALTER ROLE % NOSUPERUSER NOCREATEROLE first', runtime_name, runtime_name;
    END IF;

    IF pg_has_role(runtime_name, owner_name, 'MEMBER') THEN
        RAISE EXCEPTION 'the runtime login % is a member of the owner %, so it acts as the owner; '
            'REVOKE % FROM % first', runtime_name, owner_name, owner_name, runtime_name;
    END IF;

    -- A table's owner may disable, drop or replace its triggers without being a superuser. Owning
    -- anything here, the database included, is what this whole setup exists to prevent.
    SELECT count(*) INTO owned
    FROM pg_shdepend
    WHERE deptype = 'o' AND refobjid = runtime_login.oid
      AND dbid = (SELECT oid FROM pg_database WHERE datname = current_database());

    IF owned > 0 OR EXISTS (
        SELECT FROM pg_database WHERE datname = current_database() AND datdba = runtime_login.oid
    ) THEN
        RAISE EXCEPTION 'the runtime login % owns database % or % object(s) in it, so it could change '
            'their triggers; separate-owner.sql moves them to the owner', runtime_name, current_database(), owned;
    END IF;
END
$$;

-- The database: connect, and nothing else of the login's own. What PUBLIC holds on it (CONNECT and
-- TEMPORARY, by default) is left as it is.
SELECT format('REVOKE ALL ON DATABASE %I FROM %I', current_database(), :'runtime') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'runtime') \gexec

-- The schemas: look objects up, create none. PostgreSQL 15 and later already keep PUBLIC from
-- creating in public; on a cluster first created by an earlier version, the revoke does it.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA heimdall, public FROM :"runtime";
GRANT USAGE ON SCHEMA heimdall, public TO :"runtime";

-- The tables that exist: read and write rows, and nothing else — no TRUNCATE, REFERENCES, TRIGGER
-- or MAINTAIN. public holds only the migration history, which the start-up seeder reads to refuse a
-- schema that is behind; the API never writes it. No EXECUTE is granted: the API calls no function
-- of its own, the triggers' functions run when their triggers fire, and PUBLIC keeps the EXECUTE it
-- has on every function by default.
REVOKE ALL ON ALL TABLES IN SCHEMA heimdall, public FROM :"runtime";
REVOKE ALL ON ALL SEQUENCES IN SCHEMA heimdall, public FROM :"runtime";
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA heimdall, public FROM :"runtime";
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA heimdall TO :"runtime";
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA heimdall TO :"runtime";
GRANT SELECT ON ALL TABLES IN SCHEMA public TO :"runtime";

-- The audit trail is never deleted from. It keeps UPDATE because the retention pass writes the rows
-- it clears whole; which updates are accepted is its triggers' decision, and the login cannot change
-- them. A new deployment runs this before the first migration creates the table, and again after.
-- A later migration that adds a table the API must not write freely adds its revoke here.
SELECT format('REVOKE DELETE ON heimdall.audit_log FROM %I', :'runtime')
WHERE to_regclass('heimdall.audit_log') IS NOT NULL \gexec

-- The tables later migrations create get the same, with no grant in the migration: default
-- privileges are recorded against the role that creates the object, so they apply to everything
-- the owner creates and to nothing anyone else does. A migration run as a superuser or as the
-- runtime login is therefore not granted, and the API's first query of its tables says so.
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA heimdall REVOKE ALL ON TABLES FROM :"runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA heimdall REVOKE ALL ON SEQUENCES FROM :"runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA public REVOKE ALL ON TABLES FROM :"runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA heimdall
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA heimdall
    GRANT USAGE, SELECT ON SEQUENCES TO :"runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA public
    GRANT SELECT ON TABLES TO :"runtime";
