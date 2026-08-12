#!/usr/bin/env python3
"""Run the test suite with coverage and regenerate the HTML report the docs site publishes.

The report lives at docs/coverage-report and is served by the documentation site at
/coverage-report/. It is a generated directory and is not committed: CI runs the suites, calls this
script with --report-only, and uploads the result as an artifact that the docs workflow downloads
into place before Hugo builds. Running this script locally regenerates the same directory for
previewing, and git ignores it.

The PRO version's license is read from the REPORTGENERATOR_LICENSE environment variable when set,
and passed to ReportGenerator as -license. It is never echoed. Unset, generation still succeeds with
the free version — which is what happens on pull requests from forks, where GitHub withholds secrets
by design.

Two things here are deliberate rather than incidental, and both come from the same incident. The
project was renamed from `identity-manager-api` to `heimdall-api`; `dotnet build` only overwrites
files it produces, so the pre-rename `ArturRios.IdentityManager.*` assemblies stayed behind in every
bin/ and obj/. coverlet instruments whatever assemblies it finds in the test output directory, so
those stale ones were instrumented too and a third of the report described classes that no longer
exist. Hence:

  * --clean wipes bin/ and obj/ before building, so only current assemblies can be instrumented.
  * The report directory is always emptied before ReportGenerator writes to it, so a page for a
    class that no longer exists cannot survive from a previous run.

Usage (from anywhere in the repository):

    python scripts/coverage.py                 # full suite, regenerate the report
    python scripts/coverage.py --clean         # purge bin/obj first (after a rename or a stale build)
    python scripts/coverage.py --filter "Category=Unit"
    python scripts/coverage.py --no-report     # collect coverage but skip the HTML generation
    python scripts/coverage.py --report-only   # build the report from coverage files already on disk

Requires the ReportGenerator global tool:

    dotnet tool install --global dotnet-reportgenerator-globaltool
"""

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SOLUTION = REPO_ROOT / "src/ArturRios.Heimdall.sln"
TESTS_DIR = REPO_ROOT / "tests"
SRC_DIR = REPO_ROOT / "src"
REPORT_DIR = REPO_ROOT / "docs/coverage-report"
LICENSE_VARIABLE = "REPORTGENERATOR_LICENSE"


def run(command, echo=None, **kwargs):
    """Run a command from the repository root, echoing it first.

    `echo` overrides what is printed, which is how the license key stays out of the log.
    """
    print(f"$ {' '.join(str(part) for part in echo or command)}", flush=True)
    return subprocess.run(command, cwd=REPO_ROOT, **kwargs)


def purge_build_output():
    """Delete every bin/ and obj/ under src/ and tests/.

    `dotnet clean` removes only what the current project declares it produced, so it leaves
    assemblies from a previous project name in place. Deleting the directories is what actually
    guarantees a build cannot be polluted by them.
    """
    removed = 0

    for root in (SRC_DIR, TESTS_DIR):
        for directory in sorted(root.rglob("*")):
            if directory.is_dir() and directory.name in ("bin", "obj"):
                shutil.rmtree(directory, ignore_errors=True)
                removed += 1

    print(f"Removed {removed} bin/obj directories")


def stale_results():
    """Coverage files left over from previous runs."""
    return sorted(TESTS_DIR.glob("**/TestResults"))


def clear_previous_results():
    """Remove previous TestResults so only this run's coverage is picked up."""
    for directory in stale_results():
        shutil.rmtree(directory, ignore_errors=True)


def collect(test_filter):
    """Run the tests with the coverage collector attached."""
    command = ["dotnet", "test", str(SOLUTION), "--collect:XPlat Code Coverage"]

    if test_filter:
        command += ["--filter", test_filter]

    return run(command).returncode


def coverage_files():
    return sorted(TESTS_DIR.glob("**/TestResults/*/coverage.cobertura.xml"))


def generate_report():
    """Regenerate the HTML report from scratch."""
    files = coverage_files()

    if not files:
        print("No coverage files were produced; nothing to report on.", file=sys.stderr)
        return 1

    print(f"Found {len(files)} coverage file(s)")

    # Emptied rather than written over: ReportGenerator does not remove pages whose class has
    # since disappeared, so writing into a populated directory keeps stale pages alive forever.
    if REPORT_DIR.exists():
        shutil.rmtree(REPORT_DIR)

    command = [
        "reportgenerator",
        f"-reports:{';'.join(str(path) for path in files)}",
        f"-targetdir:{REPORT_DIR}",
        "-reporttypes:Html",
        "-title:Heimdall API",
    ]
    echo = list(command)

    # Appended only when set, and never as an empty value: `-license:` with nothing after it is
    # rejected. An unset variable is a normal, supported case — a fork's pull request cannot read
    # the repository secret, and the free version's report is better than a failed job.
    license_key = os.environ.get(LICENSE_VARIABLE, "").strip()

    if license_key:
        command.append(f"-license:{license_key}")
        echo.append("-license:***")
        print(f"Using the PRO license from {LICENSE_VARIABLE}")
    else:
        print(f"{LICENSE_VARIABLE} is not set; generating with the free version")

    return run(command, echo=echo).returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--clean", action="store_true",
                        help="purge every bin/ and obj/ before building, so no stale assembly is instrumented")
    parser.add_argument("--filter", dest="test_filter", default=None,
                        help="restrict the run, e.g. \"Category=Unit\"")
    parser.add_argument("--no-report", action="store_true",
                        help="collect coverage but do not regenerate the HTML report")
    parser.add_argument("--report-only", action="store_true",
                        help="skip the test run and build the report from coverage files already on disk")
    args = parser.parse_args()

    if args.report_only and (args.clean or args.no_report or args.test_filter):
        parser.error("--report-only runs no tests, so --clean, --no-report and --filter do not apply to it")

    # CI has already run both suites with the collector attached by the time it calls this, so
    # re-running them here would double the build minutes and, worse, throw away the functional
    # suite's coverage if the runner's Docker daemon were unavailable on the second pass.
    if not args.report_only:
        if args.clean:
            purge_build_output()

        clear_previous_results()

        if collect(args.test_filter) != 0:
            print("Tests failed; the report was not regenerated.", file=sys.stderr)
            return 1

        if args.no_report:
            return 0

    if generate_report() != 0:
        return 1

    print(f"\nReport written to {REPORT_DIR}")
    # ASCII on purpose: Python's stdout on Windows defaults to cp1252, which cannot encode an arrow
    # and raises UnicodeEncodeError — after the report has already been written, so the script would
    # fail on its very last line having done its whole job.
    print("Preview it with: hugo -s docs server  ->  http://localhost:1313/heimdall-api/coverage-report/")

    return 0


if __name__ == "__main__":
    sys.exit(main())
