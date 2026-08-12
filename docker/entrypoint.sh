#!/bin/sh
set -e

# Nothing in the API applies migrations -- Startup only seeds (DatabaseSeeder), and the seeder writes
# into tables that must already exist. A container started against an empty database therefore has to
# migrate first, which is what the bundle built into the image does here.
#
# It is opt-out (HEIMDALL_RUN_MIGRATIONS=false) rather than unconditional so a deployment that
# applies migrations out of band -- a separate step, or a second replica that must not race the first
# -- can skip it without a different image.
if [ "${HEIMDALL_RUN_MIGRATIONS:-true}" = "true" ]; then
    if [ -z "$HEIMDALL_DATA_CONNECTIONSTRING" ]; then
        echo "entrypoint: HEIMDALL_DATA_CONNECTIONSTRING is unset; cannot apply migrations." >&2
        exit 1
    fi

    echo "entrypoint: applying EF Core migrations..."
    /app/heimdall-migrate --connection "$HEIMDALL_DATA_CONNECTIONSTRING"
    echo "entrypoint: migrations up to date."
fi

exec "$@"
