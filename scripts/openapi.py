#!/usr/bin/env python3
"""Regenerate the OpenAPI document the documentation site publishes.

The document lives at docs/openapi/heimdall.json and is served by the documentation site at
/openapi/heimdall.json, where docs/content/en/docs/api-explorer.md renders it as a Swagger UI page.
So regenerating it and committing the result is how the published API reference moves.

It is produced by tools/ArturRios.Heimdall.OpenApiGen, which reflects over the Web API's controllers
without starting the Web API: no database, no environment file, no port.

That is the reason to prefer it over `dotnet swagger tofile`, which does now work but boots the real
host -- and the host seeds the database on startup, so the CLI needs a running, migrated PostgreSQL
just to emit a document that does not depend on one. The generator needs nothing but the build.

What it emits is not a second opinion. Both the generator and the running API apply the same
ArturRios.Heimdall.WebApi.Documentation.SwaggerConfiguration, so this file is byte-for-byte what a
running instance serves at /swagger/v1/swagger.json.

Usage (from anywhere in the repository):

    python scripts/openapi.py                 # rebuild the generator, rewrite the document
    python scripts/openapi.py --no-build      # reuse the last build (faster, and stale if src changed)
    python scripts/openapi.py --check         # fail if the committed document is out of date

--check is what CI would run: it regenerates into a temporary file and compares, so a controller
change that never had its document regenerated is caught rather than silently published.
"""

import argparse
import filecmp
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
GENERATOR = REPO_ROOT / "tools/ArturRios.Heimdall.OpenApiGen"
DOCUMENT = REPO_ROOT / "docs/openapi/heimdall.json"
CONFIGURATION = "Release"


def run(command):
    """Run a command from the repository root, echoing it first."""
    print(f"$ {' '.join(str(part) for part in command)}", flush=True)

    return subprocess.run(command, cwd=REPO_ROOT)


def build():
    """Build the generator, which builds the Web API it reflects over."""
    result = run(["dotnet", "build", str(GENERATOR), "-c", CONFIGURATION])

    if result.returncode != 0:
        print("\nThe generator failed to build; the document was not touched.")
        sys.exit(result.returncode)


def generate(destination):
    """Write the OpenAPI document to destination."""
    command = ["dotnet", "run", "--project", str(GENERATOR), "-c", CONFIGURATION, "--no-build", "--", str(destination)]
    result = run(command)

    if result.returncode != 0:
        print("\nThe generator failed; the document was not written.")
        sys.exit(result.returncode)


def check():
    """Regenerate into a temporary file and compare it with the committed document."""
    if not DOCUMENT.exists():
        print(f"{DOCUMENT.relative_to(REPO_ROOT)} does not exist. Run: python scripts/openapi.py")

        return 1

    with tempfile.TemporaryDirectory() as directory:
        candidate = Path(directory) / "heimdall.json"

        generate(candidate)

        if filecmp.cmp(candidate, DOCUMENT, shallow=False):
            print(f"\n{DOCUMENT.relative_to(REPO_ROOT)} is up to date.")

            return 0

    print(
        f"\n{DOCUMENT.relative_to(REPO_ROOT)} is out of date.\n"
        "Regenerate it and commit the result:\n\n    python scripts/openapi.py\n"
    )

    return 1


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--no-build", action="store_true", help="reuse the last build instead of rebuilding")
    parser.add_argument("--check", action="store_true", help="fail if the committed document is out of date")

    arguments = parser.parse_args()

    if not arguments.no_build:
        build()

    if arguments.check:
        return check()

    generate(DOCUMENT)

    print(f"\nWrote {DOCUMENT.relative_to(REPO_ROOT)}")
    print("Commit it to publish the change: the documentation site serves this file as-is.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
