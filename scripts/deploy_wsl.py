#!/usr/bin/env python3
"""Deploy the Heimdall API into a WSL distro without cloning the repository there.

The image is built on Windows with Docker Desktop, exported, and imported into the
distro's own Docker engine. Only three things ever reach the distro: the image, the
Compose file, and the environment file. No source, no SDK, no build inside WSL.

    Windows                                   WSL (Ubuntu)
    -------                                   ------------
    docker compose build   ->  image tar  ->  docker load
    docker-compose.yml     ---- copy ------>  ~/heimdall/docker-compose.yml
    docker/<env>.env       ---- copy ------>  ~/heimdall/<env>.env
                                              docker compose up -d

The Compose file keeps its `build:` section, and that is fine: Compose resolves a build
context only when it actually has to build, so a preloaded image starts even though the
distro holds no Dockerfile.

Usage (from anywhere in the repository):

    python scripts/deploy_wsl.py
    python scripts/deploy_wsl.py --env-file docker/development.env --distro Ubuntu
    python scripts/deploy_wsl.py --no-build          # reuse the image already built
    python scripts/deploy_wsl.py --dry-run           # print the commands, run nothing

Prerequisites, none of which this script can create for you:

  * Docker Desktop running on Windows, and a Docker engine running inside the distro
    (`systemctl is-active docker` -- see the Deploying with Docker page).
  * The environment file filled in. Copy docker/development.env.example to
    docker/development.env first; Compose refuses to build while a required value is empty.
  * PostgreSQL in the distro reachable from a container -- it listens on 127.0.0.1 alone
    out of the box, which no container can reach. The docs page covers both edits.
"""

import argparse
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
COMPOSE_FILE = REPO_ROOT / "docker-compose.yml"
DEFAULT_ENV_FILE = REPO_ROOT / "docker/development.env"
DEFAULT_DISTRO = "Ubuntu"
DEFAULT_REMOTE_DIR = "~/heimdall"


class DeployError(Exception):
    """A step failed in a way the operator has to resolve."""


def run(command, capture=False, dry_run=False, stdin_bytes=None):
    """Run a command, echoing it first so the script is never a black box."""
    print(f"  $ {' '.join(command)}")

    if dry_run:
        return ""

    result = subprocess.run(
        command,
        input=stdin_bytes,
        capture_output=capture,
        text=not capture and stdin_bytes is None,
    )

    if result.returncode != 0:
        detail = ""

        if capture and result.stderr:
            detail = f"\n{result.stderr.decode(errors='replace').strip()}"

        raise DeployError(f"command failed with exit code {result.returncode}{detail}")

    if capture:
        return result.stdout.decode(errors="replace")

    return ""


def wsl(distro, *arguments, **kwargs):
    """Run a command inside the distro."""
    return run(["wsl.exe", "-d", distro, "--", *arguments], **kwargs)


def preflight(distro, env_file, dry_run):
    """Fail early and by name, rather than halfway through a 300 MB transfer."""
    if not COMPOSE_FILE.exists():
        raise DeployError(f"{COMPOSE_FILE} not found -- run this from the repository")

    if not env_file.exists():
        raise DeployError(
            f"{env_file} not found. Copy the matching docker/*.env.example to it and fill "
            "it in; Compose refuses to build while a required value is empty."
        )

    if shutil.which("wsl.exe") is None:
        raise DeployError("wsl.exe not found -- this script runs from Windows")

    print("Checking Docker Desktop...")
    run(["docker", "version", "--format", "{{.Server.Version}}"], capture=True, dry_run=dry_run)

    print(f"Checking the Docker engine inside {distro}...")

    try:
        wsl(distro, "docker", "version", "--format", "{{.Server.Version}}",
            capture=True, dry_run=dry_run)
    except DeployError as error:
        raise DeployError(
            f"no working Docker engine inside {distro}: {error}\n"
            "Install Docker Engine in the distro (apt install docker-ce) and start it, or "
            "clear a stale ~/.docker/config.json left by Docker Desktop's WSL integration."
        ) from error


def image_name(env_file, dry_run):
    """Ask Compose which image the service resolves to, rather than assuming the default."""
    output = run(
        ["docker", "compose", "--env-file", str(env_file), "config", "--images"],
        capture=True,
        dry_run=dry_run,
    )

    if dry_run:
        return "heimdall-api:latest"

    images = [line.strip() for line in output.splitlines() if line.strip()]

    if not images:
        raise DeployError("Compose reported no image for the api service")

    return images[0]


def to_wsl_path(windows_path):
    """Translate C:\\Users\\... to /mnt/c/Users/... .

    Done here rather than by handing the path to the distro's own wslpath, which would be
    the obvious choice: wsl.exe eats the backslashes on the way through, so wslpath is
    handed "C:UsersArtur..." and fails on it.
    """
    path = Path(windows_path).resolve()
    drive = path.drive.rstrip(":").lower()
    remainder = "/".join(path.parts[1:])

    return f"/mnt/{drive}/{remainder}"


def copy_into_distro(distro, source, destination, dry_run):
    """Write a file into the distro through stdin.

    Piped rather than copied through /mnt so the transfer does not depend on the drive
    being mounted, and normalised to LF on the way: Compose's dotenv parser and the shell
    both read these, and a CRLF that survives into a value is invisible until something
    fails far from here.
    """
    print(f"  -> {destination}")

    if dry_run:
        return

    content = source.read_bytes().replace(b"\r\n", b"\n")

    subprocess.run(
        ["wsl.exe", "-d", distro, "--", "sh", "-c", f"cat > {destination}"],
        input=content,
        check=True,
    )


def deploy(arguments):
    env_file = Path(arguments.env_file)

    if not env_file.is_absolute():
        env_file = REPO_ROOT / env_file

    distro = arguments.distro
    remote_dir = arguments.remote_dir
    dry_run = arguments.dry_run

    preflight(distro, env_file, dry_run)

    image = image_name(env_file, dry_run)

    print(f"\nImage: {image}")

    if arguments.no_build:
        print("\nSkipping the build (--no-build); using the image already on Docker Desktop.")
    else:
        print("\nBuilding on Docker Desktop...")
        run(
            ["docker", "compose", "--env-file", str(env_file), "build"],
            dry_run=dry_run,
        )

    print(f"\nExporting the image and importing it into {distro}...")

    with tempfile.TemporaryDirectory() as directory:
        tar_path = Path(directory) / "heimdall-api.tar"

        run(["docker", "save", image, "--output", str(tar_path)], dry_run=dry_run)

        if not dry_run:
            size_mb = tar_path.stat().st_size / (1024 * 1024)
            print(f"  ({size_mb:.0f} MB)")

        wsl(distro, "docker", "load", "--input", to_wsl_path(tar_path), dry_run=dry_run)

    print(f"\nCopying the Compose file and the environment file to {remote_dir}...")

    remote_env = f"{remote_dir}/{env_file.name}"

    # Through a shell, not `wsl -- mkdir`: without one, "~" reaches mkdir as a literal
    # character and the deployment lands in a directory actually named "~".
    wsl(distro, "sh", "-c", f"mkdir -p {remote_dir}", dry_run=dry_run)
    copy_into_distro(distro, COMPOSE_FILE, f"{remote_dir}/docker-compose.yml", dry_run)
    copy_into_distro(distro, env_file, remote_env, dry_run)

    if arguments.no_start:
        print("\nStopping before start (--no-start). To bring it up yourself:")
        print(f"  wsl -d {distro} -- sh -c "
              f"'cd {remote_dir} && docker compose --env-file {env_file.name} up -d'")

        return

    print("\nStarting the service...")
    wsl(
        distro,
        "sh",
        "-c",
        f"cd {remote_dir} && docker compose --env-file {env_file.name} up -d",
        dry_run=dry_run,
    )

    print("\nContainer state:")
    wsl(
        distro,
        "sh",
        "-c",
        f"cd {remote_dir} && docker compose --env-file {env_file.name} ps",
        dry_run=dry_run,
    )

    print(
        "\nThe health check has a 30 s start period, so 'starting' is expected at first.\n"
        "Follow the start-up -- migrations run before the API listens:\n"
        f"  wsl -d {distro} -- sh -c "
        f"'cd {remote_dir} && docker compose --env-file {env_file.name} logs -f api'"
    )


def main():
    parser = argparse.ArgumentParser(
        description="Build the Heimdall API on Windows and deploy it into a WSL distro.",
    )
    parser.add_argument(
        "--distro", default=DEFAULT_DISTRO,
        help=f"WSL distro to deploy into (default: {DEFAULT_DISTRO})",
    )
    parser.add_argument(
        "--env-file", default=str(DEFAULT_ENV_FILE.relative_to(REPO_ROOT)),
        help="environment file, relative to the repository root "
             f"(default: {DEFAULT_ENV_FILE.relative_to(REPO_ROOT)})",
    )
    parser.add_argument(
        "--remote-dir", default=DEFAULT_REMOTE_DIR,
        help=f"directory inside the distro to deploy to (default: {DEFAULT_REMOTE_DIR})",
    )
    parser.add_argument(
        "--no-build", action="store_true",
        help="reuse the image already on Docker Desktop instead of rebuilding",
    )
    parser.add_argument(
        "--no-start", action="store_true",
        help="transfer everything but do not start the service",
    )
    parser.add_argument(
        "--dry-run", action="store_true",
        help="print every command without running any of them",
    )

    arguments = parser.parse_args()

    try:
        deploy(arguments)
    except DeployError as error:
        print(f"\nerror: {error}", file=sys.stderr)

        return 1
    except KeyboardInterrupt:
        print("\ninterrupted", file=sys.stderr)

        return 130

    return 0


if __name__ == "__main__":
    sys.exit(main())
