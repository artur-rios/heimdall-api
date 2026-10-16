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
# itself can run as a role that owns nothing and cannot alter the tables or their triggers (Threat
# Model TH-18; the operations guide, "Database roles"). Unset, everything works as it always has.
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

    /app/heimdall-migrate --connection "$migrations_connection"
    unset migrations_connection
    echo "entrypoint: migrations up to date."
fi

# The owner's connection string is the migrations' alone: the API never needs it, so it does not
# inherit it.
unset HEIMDALL_MIGRATIONS_CONNECTIONSTRING

exec "$@"
