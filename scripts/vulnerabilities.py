#!/usr/bin/env python3
"""Fail when any dependency has a known vulnerability.

`dotnet list package --vulnerable` reports findings on stdout and exits 0 whether it found
anything or not, so a CI step that just runs it is a step that can never fail. This runs it,
parses what it printed, and turns findings into a non-zero exit.

`--include-transitive` is not optional here. The vulnerability this project actually shipped with
was in SSH.NET, pulled in transitively; a scan of direct references only would have reported a
clean tree while the CVE sat two levels down.

Usage (from anywhere in the repository):

    python scripts/vulnerabilities.py            # scan the solution, fail on any finding
    python scripts/vulnerabilities.py --quiet    # print only the findings

Exit codes: 0 clean, 1 vulnerabilities found, 2 the scan itself could not run.

When a finding has no fixed version available -- a transitive dependency whose publisher has not
released one -- the options are to pin a patched version of the intermediate package, to pin the
transitive package directly (central package management with transitive pinning is already enabled
in Directory.Packages.props, which makes this a one-line change), or to replace the dependency.
Deleting this check is not one of the options.
"""

import argparse
import re
import subprocess
import sys
from collections import namedtuple
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SOLUTION = REPO_ROOT / "src/ArturRios.Heimdall.sln"

# Two phrasings, and the difference is not cosmetic: scanning a solution produces
#
#     The given project `ArturRios.Heimdall.WebApi` has the following vulnerable packages
#
# while scanning a single project produces
#
#     Project `vulncheck` has the following vulnerable packages
#
# -- capitalised, and without the "The given" prefix. Matching only the first form still reports
# every finding, because the findings are read from the rows, but it attributes them all to no
# project at all.
PROJECT_PATTERN = re.compile(
    r"project\s+[`'\"]?([^`'\"]+?)[`'\"]?\s+has the following vulnerable packages", re.IGNORECASE)

Finding = namedtuple("Finding", "project package resolved severity advisory")


def parse(output):
    """Extract the findings from `dotnet list package --vulnerable` output.

    Two table shapes appear, and both are handled by reading from the end of the row rather than
    counting from the start:

        > SSH.NET          2024.1.0   2024.1.0   High   https://github.com/advisories/...
        > System.Text.Json            8.0.0      High   https://github.com/advisories/...

    The first is a top-level package, which carries a requested version as well as a resolved one;
    the second is transitive and has no requested version. Severity, resolved version and advisory
    are the last three columns either way.
    """
    findings = []
    project = None

    for line in output.splitlines():
        match = PROJECT_PATTERN.search(line)

        if match:
            project = match.group(1)
            continue

        parts = line.split()

        # A row rather than a header or a framework line. The `>` marker is what `dotnet list`
        # puts on every package row and on nothing else.
        if len(parts) >= 5 and parts[0] == ">":
            findings.append(Finding(
                project=project,
                package=parts[1],
                resolved=parts[-3],
                severity=parts[-2],
                advisory=parts[-1]))

    return findings


def describe(findings):
    """One line per finding, worst first, in a form that names the fix rather than only the problem."""
    order = {"critical": 0, "high": 1, "moderate": 2, "low": 3}
    ranked = sorted(findings, key=lambda f: (order.get(f.severity.lower(), 4), f.package))

    return [f"{f.severity:<8} {f.package} {f.resolved} ({f.project}) {f.advisory}" for f in ranked]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--quiet", action="store_true", help="print only the findings, not the raw scan output")
    arguments = parser.parse_args()

    command = ["dotnet", "list", str(SOLUTION), "package", "--vulnerable", "--include-transitive"]

    print(f"$ {' '.join(command)}", flush=True)

    try:
        result = subprocess.run(command, cwd=REPO_ROOT, capture_output=True, text=True)
    except OSError as error:
        print(f"Could not run the scan: {error}", file=sys.stderr)
        return 2

    output = result.stdout + result.stderr

    if not arguments.quiet:
        print(output)

    # The command exits 0 for a clean tree and for a vulnerable one alike, so its own status says
    # nothing about findings -- but a non-zero status still means the scan itself failed (an
    # unreachable feed, an unrestorable project), and reporting that as "clean" would be worse than
    # failing, because it is the shape a silently disabled check has.
    if result.returncode != 0:
        print(f"The scan did not complete (exit {result.returncode}); no conclusion can be drawn.", file=sys.stderr)
        return 2

    findings = parse(output)

    if not findings:
        print("No known vulnerabilities in any dependency, direct or transitive.")
        return 0

    print(f"\n{len(findings)} vulnerable package reference(s):\n", file=sys.stderr)

    for line in describe(findings):
        print(f"  {line}", file=sys.stderr)

    return 1


if __name__ == "__main__":
    sys.exit(main())
