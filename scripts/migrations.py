#!/usr/bin/env python3
"""Interactive menu for the Identity Manager's EF Core migrations.

Lists, creates and applies migrations for ArturRios.IdentityManager.Data, loading the
connection string from one of the environment files under the Web API's Environments folder.

Usage (from anywhere in the repository):

    python scripts/migrations.py

Requires the pinned EF tool -- run `dotnet tool restore` once after cloning.
"""

import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
ENVIRONMENTS_DIR = REPO_ROOT / "src/Presentation/ArturRios.IdentityManager.WebApi/Environments"
DATA_PROJECT = REPO_ROOT / "src/Infrastructure/ArturRios.IdentityManager.Data"
STARTUP_PROJECT = DATA_PROJECT

CONNECTION_STRING_VARIABLE = "IDENTITY_MANAGER_DATA_CONNECTIONSTRING"
SECRET_KEYS = {"password", "pwd"}
MIGRATION_NAME_PATTERN = re.compile(r"^[A-Za-z][A-Za-z0-9]*$")


def parse_env_file(text):
    """Parse .env content into a dict, tolerating a BOM, quotes, comments and blank lines."""
    values = {}

    for raw_line in text.lstrip("﻿").splitlines():
        line = raw_line.strip()

        if not line or line.startswith("#"):
            continue

        key, separator, value = line.partition("=")

        if not separator:
            continue

        key = key.strip()
        value = value.strip()

        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]

        values[key] = value

    return values


def describe_connection(connection_string):
    """Render a connection string with its secrets masked, so it is safe to print."""
    segments = []

    for raw_segment in connection_string.split(";"):
        segment = raw_segment.strip()

        if not segment:
            continue

        key, separator, _ = segment.partition("=")

        if separator and key.strip().lower() in SECRET_KEYS:
            segments.append(f"{key.strip()}=***")
        else:
            segments.append(segment)

    return ";".join(segments)


def prompt_yes_no(question):
    return input(f"{question} [y/N] ").strip().lower() in {"y", "yes"}


def choose_environment_file():
    """Ask which environment file to load. Returns a Path, or None when we cannot continue."""
    if not ENVIRONMENTS_DIR.is_dir():
        print(f"Environments folder not found: {ENVIRONMENTS_DIR}")
        return None

    files = sorted(path for path in ENVIRONMENTS_DIR.glob(".env*") if path.is_file())

    if not files:
        print(f"No environment files found in {ENVIRONMENTS_DIR}.")
        return None

    if [path.name for path in files] == [".env"]:
        template = files[0]
        print(f"Only the tracked template {template.name} exists; it holds placeholders, not real values.")

        if prompt_yes_no("Create .env.local from it now?"):
            local = ENVIRONMENTS_DIR / ".env.local"
            shutil.copyfile(template, local)
            print(f"Created {local}. Fill in the values, then run this script again.")

        return None

    print("\nEnvironment files:")

    for index, path in enumerate(files, start=1):
        print(f"  {index}) {path.name}")

    choice = input("Choose an environment file: ").strip()

    if not choice.isdigit() or not 1 <= int(choice) <= len(files):
        print("Invalid choice.")
        return None

    return files[int(choice) - 1]


def ensure_ef_tool(environ):
    result = subprocess.run(
        ["dotnet", "ef", "--version"],
        cwd=REPO_ROOT,
        env=environ,
        capture_output=True,
        text=True,
    )

    if result.returncode != 0:
        print("dotnet ef is not available. Install the pinned tool with:\n\n    dotnet tool restore\n")
        return False

    print(f"Using dotnet ef {result.stdout.strip().splitlines()[-1]}")

    return True


def run_ef(arguments, environ):
    command = [
        "dotnet",
        "ef",
        *arguments,
        "--project",
        str(DATA_PROJECT),
        "--startup-project",
        str(STARTUP_PROJECT),
    ]

    # Flush before handing the terminal to the subprocess, or our echo lands after its output.
    print(f"\n$ {' '.join(command)}\n", flush=True)

    result = subprocess.run(command, cwd=REPO_ROOT, env=environ)

    if result.returncode != 0:
        print(f"\ndotnet ef exited with code {result.returncode}.")

    return result.returncode


def create_migration(environ):
    name = input("Migration name (PascalCase, letters and digits only): ").strip()

    if not MIGRATION_NAME_PATTERN.match(name):
        print("Invalid name. Use letters and digits, starting with a letter -- for example AddScopeIndex.")
        return

    run_ef(["migrations", "add", name, "--output-dir", "Migrations"], environ)


def apply_migrations(environ, connection_string):
    print(f"\nTarget: {describe_connection(connection_string)}")

    if not prompt_yes_no("Apply all pending migrations to this database?"):
        print("Cancelled.")
        return

    run_ef(["database", "update"], environ)


def main():
    environment_file = choose_environment_file()

    if environment_file is None:
        return 1

    variables = parse_env_file(environment_file.read_text(encoding="utf-8"))
    connection_string = variables.get(CONNECTION_STRING_VARIABLE, "")

    if not connection_string.strip():
        print(f"{environment_file.name} does not set {CONNECTION_STRING_VARIABLE}.")
        return 1

    environ = {**os.environ, **variables}

    print(f"\nLoaded {environment_file.name} -> {describe_connection(connection_string)}")

    if not ensure_ef_tool(environ):
        return 1

    while True:
        print("\n  1) List migrations")
        print("  2) Create a migration")
        print("  3) Apply migrations")
        print("  4) Exit")

        choice = input("Choose an option: ").strip()

        if choice == "1":
            run_ef(["migrations", "list"], environ)
        elif choice == "2":
            create_migration(environ)
        elif choice == "3":
            apply_migrations(environ, connection_string)
        elif choice == "4":
            return 0
        else:
            print("Unknown option.")


if __name__ == "__main__":
    sys.exit(main())
