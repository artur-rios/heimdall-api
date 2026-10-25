#!/usr/bin/env bash
# The `check` check: the committed OpenAPI document (docs/openapi/heimdall.json) is up to date. It is a generated file
# that is committed, so it can go stale: a change to a controller, a command, a query output or the Roles enum
# changes what the document should say, and the docs site only rebuilds on a push to main -- a stale document would
# keep being published as if it were right.
#
# scripts/openapi.py --check builds the generator (and so the Web API it reflects over), regenerates the document into
# a temporary file and compares the bytes. It needs no database and no running API: that is why generation goes
# through tools/ArturRios.Heimdall.OpenApiGen and not `dotnet swagger tofile`, which boots the real host.
# yggdrasil.yml runs it in the pinned .NET SDK image from the repository's root, only when one of the paths it lists
# changed.
set -euo pipefail

# The repository's root, wherever this is run from.
cd "$(dirname "${BASH_SOURCE[0]}")/.."

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# shellcheck source=ci/python.sh
. ci/python.sh
ensure_python3

python3 scripts/openapi.py --check
