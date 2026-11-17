#!/usr/bin/env bash
# The `test` check: the whole suite, then the coverage report and its minimum. yggdrasil.yml runs it in the pinned
# .NET SDK image from the repository's root, with the CI engine's socket (`docker: true`): the functional tests start
# their PostgreSQL containers through Testcontainers.
#
# Unit and functional tests run in one pass, every test project in a process of its own and all of them side by side,
# after one restore and one build. No path filter: a test suite runs for every change.
#
# The coverage report is written to docs/coverage-report (scripts/coverage.py), which the check archives and
# heimdall-docs publishes. The report is written before the minimum is
# evaluated, so a build that fails the minimum still has the report that shows why.
#
# REPORTGENERATOR_LICENSE, when set and not empty, is ReportGenerator's PRO license (yggdrasil.yml binds the Jenkins
# credential reportgenerator-license to it). Without it -- a renovate/* build binds no credential, nor does a run on
# your machine -- scripts/coverage.py makes the free report and says so. It is never printed.
set -euo pipefail

# Renovate follows this version (the comment below); it is a tool, so there is no checksum to update with it.
# renovate: datasource=nuget depName=dotnet-reportgenerator-globaltool
REPORTGENERATOR_VERSION=5.6.0
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

# Check formatting. Fails when `dotnet format` (whitespace, code style and analyzers, at the severities .editorconfig
# sets) would change a file; `dotnet format src/ArturRios.Heimdall.sln --exclude-diagnostics CS8618` applies the same
# fixes. CS8618 is left out: dotnet format does not run EF Core's suppressor for uninitialized DbSet properties, so it
# reports AppDbContext's, which the build does not, and its fixes (`required`, nullable) would change the code.
dotnet format "$SOLUTION" --verify-no-changes --no-restore --exclude-diagnostics CS8618

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

# Every test has a Category trait. The run below takes every test, but the documented way to run one kind
# (`--filter "Category=Unit"` or `"Category=Functional"`, CONTRIBUTING.md) selects by it, so a test without one would
# be missed by both.
uncategorized=$(dotnet test "$SOLUTION" --configuration Release --no-build --list-tests \
  --filter "Category!=Unit&Category!=Functional" | sed -n 's/^    //p')
if [ -n "$uncategorized" ]; then
  echo "ci/test.sh: these tests have no Category trait (Unit or Functional), so neither category filter runs them:" >&2
  echo "$uncategorized" >&2
  exit 1
fi

# Every test once, unit and functional alike: each test project in a process of its own, all of them side by side.
# (A unit pass over the solution and then a functional pass used to run one after the other, starting every test
# process twice.) The functional tests start PostgreSQL through Testcontainers: the CI engine's socket
# (`docker: true`). The [UnitFact] / [FunctionalFact] attributes of ArturRios.Util.Test give each test its Category
# trait, which the check above holds every test to. Each run writes its .trx (tests/default.runsettings) and a
# coverage.cobertura.xml under its project's TestResults/, which the report below merges.
#
# WebApi.Tests runs as three processes. Its functional tests configure the API through process-wide environment
# variables (PostgresFixture), so one process runs those classes one at a time, about two minutes of the suite;
# processes of their own, each with its own PostgreSQL container, run together. The shards split the classes by the
# start of their name, balanced on the measured durations, and the last is everything the first two are not, so every
# test runs exactly once and a new class always lands in one. Three, not more: every host a functional test starts
# stays in memory until its process ends (see PostgresFixture), so the shards share the one process's peak rather
# than add to it, but each process and its database still costs its own few hundred megabytes. Each shard runs from a
# copy of the build output of its own, since the coverage collector instruments the assemblies in place and restores
# them afterwards.
WEBAPI_TESTS=ArturRios.Heimdall.WebApi.Tests
classes_starting() { # <prefix>...: a filter matching the WebApi test classes whose names start with any prefix
  local prefix filter=""
  for prefix in "$@"; do
    filter+="${filter:+|}FullyQualifiedName~$WEBAPI_TESTS.$prefix"
  done
  echo "$filter"
}
not_starting() { # <prefix>...: a filter matching the WebApi test classes whose names start with none of them
  classes_starting "$@" | sed 's/|/\&/g; s/~/!~/g'
}
WEBAPI_SHARDS=(
  "$(classes_starting Auth T U V W X Y Z)"
  "(FullyQualifiedName~$WEBAPI_TESTS.A&FullyQualifiedName!~$WEBAPI_TESTS.Auth)|$(classes_starting B C D E F G H I J K L M N O Q R)"
  "$(not_starting A B C D E F G H I J K L M N O Q R T U V W X Y Z)"
)
RUN_LOGS=$(mktemp -d)
CLEANUP_DIRS=()
cleanup() {
  rm -rf "$RUN_LOGS" "${CLEANUP_DIRS[@]}"
}
trap cleanup EXIT
run_tests() { # <project> <run name> [dotnet test arguments...]
  local project=$1 name=$2
  shift 2
  dotnet test "$project" --configuration Release --no-build \
    --logger "trx;LogFilePrefix=$name" \
    --results-directory "$(dirname "$project")/TestResults" \
    --collect:"XPlat Code Coverage" "$@" >"$RUN_LOGS/$name.log" 2>&1
}
runs=()
for project in $(find tests -name '*.Tests.csproj' | sort); do
  name=$(basename "$project" .csproj)
  if [ "$name" = "$WEBAPI_TESTS" ]; then
    output=$(dirname "$project")/bin/Release/net10.0
    for shard in "${!WEBAPI_SHARDS[@]}"; do
      # Inside the project's own bin/ (ignored by git), not a temporary directory: some tests find the repository by
      # walking up from their assembly's directory (tests/.../Support/Repository.cs). OutputPath must be absolute, or
      # MSBuild reads it relative to the project.
      copy=$PWD/$(dirname "$project")/bin/shard-$((shard + 1))
      rm -rf "$copy"
      cp -R "$output" "$copy"
      CLEANUP_DIRS+=("$copy")
      # The project still names the run, so the coverage collector is found; OutputPath points it at the copy.
      run_tests "$project" "$name.$((shard + 1))" -p:OutputPath="$copy/" -p:AppendTargetFrameworkToOutputPath=false \
        --filter "${WEBAPI_SHARDS[$shard]}" &
      runs+=("$!:$name.$((shard + 1))")
    done
  else
    run_tests "$project" "$name" &
    runs+=("$!:$name")
  fi
done
failed=0
for run in "${runs[@]}"; do
  if wait "${run%%:*}"; then status=passed; else status=FAILED; failed=1; fi
  echo "== ${run#*:}: $status"
  # Each run's output in one piece, rather than the runs' lines interleaved.
  cat "$RUN_LOGS/${run#*:}.log"
done
[ "$failed" -eq 0 ]

# scripts/coverage.py calls the tool by name, so it is put on the PATH. It is installed into a directory of its own,
# not the user's global tools: the check runs as whoever the CI engine runs it as, with no promise about a writable
# home.
tools=$(mktemp -d)
CLEANUP_DIRS+=("$tools")
dotnet tool install dotnet-reportgenerator-globaltool --version "$REPORTGENERATOR_VERSION" --tool-path "$tools"
export PATH="$tools:$PATH"

# --report-only builds the HTML from the coverage files the runs above wrote and fails when merged line coverage
# is below the minimum in scripts/coverage.py, so a local run and this one answer the same question.
python3 scripts/coverage.py --report-only
