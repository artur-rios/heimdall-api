#!/bin/sh
set -e

# Nothing in the API applies migrations -- Startup only seeds (DatabaseSeeder), and the seeder writes
# into tables that must already exist. A container started against an empty database therefore has to
# migrate first, which is what the bundle built into the image does here.
#
# It is opt-out (HEIMDALL_RUN_MIGRATIONS=false) rather than unconditional so a deployment that
# applies migrations out of band -- a separate step, or a second replica that must not race the first
# -- can skip it without a different image.
#
# The bundle connects with HEIMDALL_MIGRATIONS_CONNECTIONSTRING when it is set, and with the API's own
# HEIMDALL_DATA_CONNECTIONSTRING otherwise. Set, it names the role that owns the schema, so the API
# itself can run as a role that owns nothing (NFR-29; the operations guide, "Database roles"). Unset,
# everything works as it always has.
#
# The connection string reaches the bundle through its environment, never its command line: a
# process's arguments can be listed by any user on the host, its environment only by its owner. The bundle reads
# HEIMDALL_DATA_CONNECTIONSTRING through DesignTimeDbContextFactory, so that is the name it is given,
# for the bundle's process alone -- the API below still gets its own.
if [ "${HEIMDALL_RUN_MIGRATIONS:-true}" = "true" ]; then
    if [ -n "$HEIMDALL_MIGRATIONS_CONNECTIONSTRING" ]; then
        migrations_connection="$HEIMDALL_MIGRATIONS_CONNECTIONSTRING"
        echo "entrypoint: applying EF Core migrations with HEIMDALL_MIGRATIONS_CONNECTIONSTRING..."
    elif [ -n "$HEIMDALL_DATA_CONNECTIONSTRING" ]; then
        migrations_connection="$HEIMDALL_DATA_CONNECTIONSTRING"
        echo "entrypoint: applying EF Core migrations with HEIMDALL_DATA_CONNECTIONSTRING..."
    else
        echo "entrypoint: neither HEIMDALL_MIGRATIONS_CONNECTIONSTRING nor HEIMDALL_DATA_CONNECTIONSTRING is set; cannot apply migrations." >&2
        exit 1
    fi

    HEIMDALL_DATA_CONNECTIONSTRING="$migrations_connection" /app/heimdall-migrate
    unset migrations_connection
    echo "entrypoint: migrations up to date."
fi

# The owner's connection string is the migrations' alone: the API never needs it, so it does not
# inherit it.
unset HEIMDALL_MIGRATIONS_CONNECTIONSTRING

exec "$@"
