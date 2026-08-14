#!/usr/bin/env python3
"""Drive a running Heimdall deployment under load and write the report NFR-05 quotes.

NFR-05 promised a response time "under normal load" while nothing in the repository generated any,
so the phrase could be neither met nor missed. This runs tools/ArturRios.Heimdall.LoadTest against a
deployment you point it at, and the requirement quotes what it measured.

It is not part of `dotnet test` and never will be. A load run needs a deployment, takes minutes, and
its numbers depend on the machine it is run from -- none of which belongs in a suite that gates a
merge. The measurement suite in the functional tests covers the single-caller figures; this covers
what happens when many callers arrive at once.

Usage (from anywhere in the repository):

    python scripts/load_test.py --url http://localhost:8080 \\
        --email admin@example.com --password '...'

    python scripts/load_test.py --url ... --email ... --password '...' \\
        --concurrency 128 --seconds 60

The account must be able to log in without a second factor: a load run cannot answer a two-factor
challenge, and the tool refuses rather than pretending it can.

Point this only at a deployment you are allowed to load. It issues as many requests as it can for
the whole duration, against every scenario in turn.
"""

import argparse
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
TOOL = REPO_ROOT / "tools/ArturRios.Heimdall.LoadTest"
# Alongside the response-time measurements, in the ignored directory: a load report is a reading
# somebody took on one machine, not a file the repository should carry a stale copy of. What gets
# committed is the conclusion, in SRD 6.3.
DEFAULT_REPORT = REPO_ROOT / "TestResults/load-test.md"
CONFIGURATION = "Release"


def run(command):
    """Run a command from the repository root, echoing it first with the password masked."""
    shown = []
    mask_next = False

    for part in command:
        if mask_next:
            shown.append("***")
            mask_next = False
        else:
            shown.append(str(part))
            mask_next = part == "--password"

    print(f"$ {' '.join(shown)}", flush=True)

    return subprocess.run(command, cwd=REPO_ROOT)


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", required=True, help="base URL of the deployment to load, e.g. http://localhost:8080")
    parser.add_argument("--email", required=True, help="an account that can log in without a second factor")
    parser.add_argument("--password", required=True, help="that account's password")
    parser.add_argument("--scope", default=None, help="scope PublicId, when the account is a User")
    parser.add_argument("--concurrency", type=int, default=32, help="callers in flight (default 32)")
    parser.add_argument("--seconds", type=int, default=30, help="duration per scenario (default 30)")
    parser.add_argument("--report", default=str(DEFAULT_REPORT),
                        help=f"where to write the report (default {DEFAULT_REPORT.relative_to(REPO_ROOT)})")
    parser.add_argument("--no-build", action="store_true", help="reuse the last build")
    arguments = parser.parse_args()

    if not arguments.no_build:
        if run(["dotnet", "build", str(TOOL), "-c", CONFIGURATION]).returncode != 0:
            print("\nThe load tool failed to build; nothing was run.", file=sys.stderr)
            return 1

    command = [
        "dotnet", "run", "--project", str(TOOL), "-c", CONFIGURATION, "--no-build", "--",
        "--url", arguments.url,
        "--email", arguments.email,
        "--password", arguments.password,
        "--concurrency", str(arguments.concurrency),
        "--seconds", str(arguments.seconds),
        "--report", arguments.report,
    ]

    if arguments.scope:
        command += ["--scope", arguments.scope]

    result = run(command)

    if result.returncode != 0:
        # The tool exits non-zero when any request faulted or answered 5xx, which is a result worth
        # failing on: a run with errors in it is not a baseline anybody should quote.
        print("\nThe load run reported failures; see the output above.", file=sys.stderr)

    return result.returncode


if __name__ == "__main__":
    sys.exit(main())
