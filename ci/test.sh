#!/usr/bin/env bash
# The `test` check: the whole suite, as the old Tests workflow's `test` job ran it, then the coverage report and its
# minimum. yggdrasil.yml runs it in the pinned .NET SDK image from the repository's root, with the CI engine's socket
# (`docker: true`): the functional suite starts a PostgreSQL container per run through Testcontainers.
#
# The two suites are steps of one script rather than two checks: they share one restore and one build, and the unit
# suite runs first so a broken unit test is reported before the functional suite spends minutes pulling and starting
# PostgreSQL. No path filter: a test suite runs for every change.
#
# The coverage report is written to docs/coverage-report (scripts/coverage.py), which the check archives and the
# docs site is built with (`deploy.inputs: [test]`, docs.Dockerfile). The report is written before the minimum is
# evaluated, so a build that fails the minimum still has the report that shows why.
#
# REPORTGENERATOR_LICENSE, when set and not empty, is ReportGenerator's PRO license (yggdrasil.yml binds the Jenkins
# credential reportgenerator-license to it). Without it -- a renovate/* build binds no credential, nor does a run on
# your machine -- scripts/coverage.py makes the free report and says so. It is never printed.
set -euo pipefail

REPORTGENERATOR_VERSION=5.5.11
SOLUTION=src/ArturRios.Heimdall.sln

# The repository's root, wherever this is run from.
cd "$(dirname "${BASH_SOURCE[0]}")/.."

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# shellcheck source=ci/python.sh
. ci/python.sh
ensure_python3

# scripts/test_migrations.py and the other test_*.py cover the helpers' pure functions -- the .env parser, the
# connection-string masking that keeps a password out of the console, the vulnerability parser. First, because it
# costs milliseconds and needs no build.
python3 -m unittest discover -s scripts -p "test_*.py" --verbose

dotnet restore "$SOLUTION"

# `dotnet list package --vulnerable` exits 0 whether or not it found anything, so the script parses what it printed.
# Straight after restore and before the build: it needs the resolved graph and nothing else. It runs on every build,
# not only when a package file changes, since an advisory arrives on its own schedule.
python3 scripts/vulnerabilities.py

# Built once so both test runs can use --no-build.
dotnet build "$SOLUTION" --configuration Release --no-restore

# A change to an entity or its map committed without `dotnet ef migrations add` builds, and fails on the first
# deployment that needs the column. The command reads no database, but the design-time factory insists on a
# connection string, so a placeholder is passed: nothing connects to it. The tool is the version
# .config/dotnet-tools.json pins.
export HEIMDALL_DATA_CONNECTIONSTRING='Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x'
dotnet tool restore
dotnet ef migrations has-pending-model-changes \
  --project src/Infrastructure/ArturRios.Heimdall.Data \
  --startup-project src/Infrastructure/ArturRios.Heimdall.Data \
  --configuration Release --no-build
unset HEIMDALL_DATA_CONNECTIONSTRING

# The two runs below select by Category trait, so a test without one would run in neither.
uncategorized=$(dotnet test "$SOLUTION" --configuration Release --no-build --list-tests \
  --filter "Category!=Unit&Category!=Functional" | sed -n 's/^    //p')
if [ -n "$uncategorized" ]; then
  echo "ci/test.sh: these tests have no Category trait (Unit or Functional), so no test step runs them:" >&2
  echo "$uncategorized" >&2
  exit 1
fi

# The Category trait comes from the [UnitFact] / [UnitTheory] attributes in ArturRios.Util.Test. Both suites collect
# coverage (a coverage.cobertura.xml per project under TestResults/) and the report below merges every file; every
# project also writes a .trx next to it (tests/default.runsettings), which the check archives.
dotnet test "$SOLUTION" --configuration Release --no-build \
  --filter "Category=Unit" \
  --collect:"XPlat Code Coverage"

# These start a real PostgreSQL container per run through Testcontainers: the CI engine's socket (`docker: true`).
dotnet test "$SOLUTION" --configuration Release --no-build \
  --filter "Category=Functional" \
  --collect:"XPlat Code Coverage"

# scripts/coverage.py calls the tool by name, so it is put on the PATH. It is installed into a directory of its own,
# not the user's global tools: the check runs as whoever the CI engine runs it as, with no promise about a writable
# home.
tools=$(mktemp -d)
trap 'rm -rf "$tools"' EXIT
dotnet tool install dotnet-reportgenerator-globaltool --version "$REPORTGENERATOR_VERSION" --tool-path "$tools"
export PATH="$tools:$PATH"

# --report-only builds the HTML from the coverage files the two runs above wrote and fails when merged line coverage
# is below the minimum in scripts/coverage.py, so a local run and this one answer the same question.
python3 scripts/coverage.py --report-only
