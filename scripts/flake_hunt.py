#!/usr/bin/env python3
"""Re-run the test suite until an intermittent failure shows itself, then name it.

A test that fails once in tens of runs is invisible to a single `dotnet test`: the console prints a
count, the run is repeated, it passes, and the failing test's name is gone. This runs the suite in a
loop, collects the .trx that tests/default.runsettings already produces, and reports every test that
failed in any run.

Usage (from anywhere in the repository):

    python scripts/flake_hunt.py                # 10 runs of the whole solution, stop at the first failure
    python scripts/flake_hunt.py --runs 25      # more attempts for a rarer failure
    python scripts/flake_hunt.py --keep-going   # run them all even after a failure, to measure a rate
    python scripts/flake_hunt.py --filter "Category=Functional"

Run the whole solution rather than one project when hunting: a failure that only appears under the
CPU contention of parallel test projects will not reproduce from the project alone.
"""

import argparse
import subprocess
import sys
import xml.etree.ElementTree as ElementTree
from collections import Counter
from datetime import datetime
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SOLUTION = REPO_ROOT / "src/ArturRios.Heimdall.sln"
TESTS_DIR = REPO_ROOT / "tests"
TRX_NAMESPACE = {"trx": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def trx_files():
    """Every .trx currently sitting in a project's TestResults folder."""
    return set(TESTS_DIR.glob("**/TestResults/*.trx"))


def failed_tests(paths):
    """Names of the tests recorded as failed across the given .trx files."""
    names = []

    for path in paths:
        try:
            root = ElementTree.parse(path).getroot()
        except ElementTree.ParseError:
            # A run killed mid-write leaves a truncated file; it has nothing to tell us.
            continue

        for result in root.findall(".//trx:UnitTestResult", TRX_NAMESPACE):
            if result.get("outcome") == "Failed":
                names.append(result.get("testName", "<unnamed>"))

    return names


def run_once(test_filter):
    """Run the suite once. Returns (succeeded, trx files this run produced)."""
    before = trx_files()

    command = ["dotnet", "test", str(SOLUTION)]

    if test_filter:
        command += ["--filter", test_filter]

    completed = subprocess.run(command, cwd=REPO_ROOT, capture_output=True, text=True)

    for line in completed.stdout.splitlines():
        if line.startswith(("Passed!", "Failed!")):
            print(f"    {line.strip()}")

    return completed.returncode == 0, trx_files() - before


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--runs", type=int, default=10, help="how many times to run the suite (default 10)")
    parser.add_argument("--filter", dest="test_filter", default=None, help="a dotnet test --filter expression")
    parser.add_argument(
        "--keep-going", action="store_true", help="keep running after a failure instead of stopping at the first")
    arguments = parser.parse_args()

    if not SOLUTION.exists():
        print(f"Solution not found at {SOLUTION}", file=sys.stderr)
        return 1

    print(f"Running the suite {arguments.runs} time(s). Ctrl-C stops early; results so far are still reported.\n")

    produced = set()
    failed_runs = []

    try:
        for attempt in range(1, arguments.runs + 1):
            started = datetime.now().strftime("%H:%M:%S")
            print(f"[{started}] run {attempt}/{arguments.runs}")

            succeeded, new_files = run_once(arguments.test_filter)
            produced |= new_files

            if succeeded:
                continue

            failed_runs.append(attempt)
            print(f"    !! run {attempt} failed")

            if not arguments.keep_going:
                break
    except KeyboardInterrupt:
        print("\nInterrupted.")

    names = Counter(failed_tests(produced))

    print(f"\n{'-' * 70}")

    if not names:
        if failed_runs:
            # The suite failed without any test failing -- a build break or a crashed host.
            print(f"Runs {failed_runs} failed, but no .trx records a failed test.")
            print("Look at the console output of that run: this is usually a build error or a crashed test host.")
        else:
            print(f"No failures in {arguments.runs} run(s). The suite did not reproduce a flake this time.")
        return 1 if failed_runs else 0

    print(f"Failing tests across {len(failed_runs)} failed run(s):\n")

    for name, count in names.most_common():
        print(f"  {count:>3}x  {name}")

    return 1


if __name__ == "__main__":
    sys.exit(main())
