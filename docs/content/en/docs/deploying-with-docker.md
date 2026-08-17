+++
title = 'Deploying with Docker'
linkTitle = 'Deploying with Docker'
weight = 75
description = 'Step by step: publishing the API with Docker Desktop on Windows and with Docker Engine inside WSL — including what the PostgreSQL on each host has to allow.'
+++

This is the walkthrough for the two environments run on a developer machine — **Local** (Docker
Desktop on Windows) and **Development** (Docker Engine inside the WSL Ubuntu distro). Both use the
same image and the same [`docker-compose.yml`](https://github.com/artur-rios/heimdall-api/blob/main/docker-compose.yml);
what differs between them is the env file and, mostly, what the host's PostgreSQL has to be told to
allow. **Production** follows the same steps as Development with `docker/production.env` — the
differences are called out at the end.

## What is being deployed

One service, `api`, built from the repository's [`Dockerfile`](https://github.com/artur-rios/heimdall-api/blob/main/Dockerfile):

- A multi-stage build that publishes the Web API **and** an EF Core migrations bundle. The bundle is
  a plain executable, so the runtime image never needs the SDK or `dotnet-ef`.
- The entrypoint applies pending migrations and only then starts the API — which is what lets a
  container be pointed at an empty database. Set `HEIMDALL_RUN_MIGRATIONS=false` to apply them out of
  band instead; see [Operations](../operations/#migrations).
- The container drops to a non-root user, writes logs to a named volume, and answers a health check
  on `/healthcheck` — Compose reports the container healthy only once the API answers.

**PostgreSQL is deliberately not a service in the Compose file.** Every environment already runs one
instance shared by several services, this deployment owning a single database of its own. So the
first half of each walkthrough below is about that instance, not about Docker.

{{% alert title="Two engines, two image stores" color="info" %}}
Docker Desktop on Windows and Docker Engine inside the Ubuntu distro are **separate daemons with
separate image stores**. An image built on Windows is not visible in WSL and vice versa; `docker ps`
in one never shows the other's containers. The two deployments are fully independent — which is why
they can both be up at once, on their own databases, without colliding.
{{% /alert %}}

## Windows — the Local environment

Docker Desktop, against the PostgreSQL installed on Windows.

### 1. Check the engine

In PowerShell, from the repository root:

```powershell
docker version --format '{{.Server.Version}}'
docker context ls
```

The context marked `*` should be `desktop-linux`. If the command fails, start Docker Desktop and wait
for the whale icon to stop animating.

### 2. Create the database and its login

Each service on the shared instance gets its own database and its own role. With `psql` on `PATH`
(`C:\Program Files\PostgreSQL\18\bin`), as the `postgres` superuser:

```powershell
psql -U postgres -c "CREATE ROLE heimdall_svc LOGIN PASSWORD '<pick-a-password>';"
psql -U postgres -c "CREATE DATABASE heimdall_local OWNER heimdall_svc;"
```

{{% alert title="Do not name the login heimdall" color="warning" %}}
The entities live in a schema called `heimdall`, and PostgreSQL's default search path — `"$user",
public` — makes the *login's* name a schema lookup. A login named `heimdall` sends the second run
looking for its migration history in that schema, where the first run wrote none: it concludes
nothing was ever applied and dies on `relation "role" already exists`. Compose pins `Search
Path=public` for exactly this reason, but the safest thing is still to call the login something else.
{{% /alert %}}

### 3. Confirm the container can reach it

Docker Desktop forwards a container's connection to `host.docker.internal` through its own VM, and
the Windows PostgreSQL sees it arrive **from `127.0.0.1`** — so the stock `pg_hba.conf`, which allows
`host all all 127.0.0.1/32`, already covers it. Nothing to change here, and no Windows Firewall rule
to add either, because nothing crosses a real network interface.

Prove it before going further, rather than discovering it from a container that will not start:

```powershell
docker run --rm --add-host host.docker.internal:host-gateway alpine sh -c 'nc -z -w 3 host.docker.internal 5432 && echo reachable || echo unreachable'
```

And that the credentials themselves work, end to end:

```powershell
docker run --rm --add-host host.docker.internal:host-gateway postgres:18-alpine psql "postgresql://heimdall_svc:<password>@host.docker.internal:5432/heimdall_local" -c "select 1"
```

A row of `1` means the container has everything it needs. `no pg_hba.conf entry for host "<address>"`
means the connection arrived from an address the rules do not cover — add `host heimdall_local
heimdall_svc <that-address>/32 scram-sha-256` to `pg_hba.conf` and reload the service.

### 4. Write the env file

```powershell
Copy-Item docker/local.env.example docker/local.env -Confirm
```

`-Confirm` is not decoration: the template's values are empty, so re-running this over a file already
filled in silently un-fills it, and the next `up` fails with `required variable ... is missing a
value` — which reads like the file was never written rather than like it was overwritten.

`docker/local.env` is gitignored — it holds the database password, the token signing secret and the
master user's credentials. Open it and fill in at least:

| Variable | Value |
| --- | --- |
| `DB_USER` / `DB_PASSWORD` | The login from step 2 |
| `DB_NAME` | `heimdall_local` |
| `HEIMDALL_AUTH_TOKEN_SECRET` | A long random value, unique to this environment |
| `HEIMDALL_MASTER_USER_NAME` / `_EMAIL` / `_PASSWORD` | The first system administrator, seeded on the first run against an empty database |

`DB_HOST=host.docker.internal` is already correct for Docker Desktop. To generate the secret:

```powershell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))
```

Every other variable has a default that suits a local deployment: Swagger and the developer exception
page are on, and verification and reset e-mails are written to the log instead of sent, because
`ASPNETCORE_ENVIRONMENT=Development` and the Mailgun variables are empty.

{{% alert title="CORS is empty by default" color="info" %}}
`HEIMDALL_CORS_ALLOWED_ORIGINS` unset refuses every cross-origin request, which is the right default
for an API that hands out credentials. A browser front end will not reach this deployment until its
origin is listed — scheme, host and port exactly as the browser sends them, e.g.
`http://localhost:3000`.
{{% /alert %}}

### 5. Build and start

```powershell
docker compose --env-file docker/local.env up -d --build
```

The first build takes a few minutes — it restores, publishes, and builds the migrations bundle.
Later builds reuse the layer cache, and a source-only change re-runs neither the tool restore nor the
package restore.

### 6. Verify

```powershell
docker compose --env-file docker/local.env ps
```

Wait for `STATUS` to read `Up (healthy)` — the health check has a 30 s start period, so `starting` is
expected at first. Then:

```powershell
curl.exe http://localhost:8080/healthcheck
```

The start-up log should show the migrations applied and then the API listening:

```powershell
docker compose --env-file docker/local.env logs -f api
```

Swagger UI is at <http://localhost:8080/swagger>, and the master user from the env file can log in at
`POST /api/auth/login`. On an empty database the entrypoint's migration step is where a bad
connection string surfaces — read the first twenty lines of the log before anything else.

### 7. Day-to-day

| Task | Command |
| --- | --- |
| Follow the logs | `docker compose --env-file docker/local.env logs -f api` |
| Restart after an env change | `docker compose --env-file docker/local.env up -d` |
| Rebuild after a code change | `docker compose --env-file docker/local.env up -d --build` |
| Stop, keeping the log volume | `docker compose --env-file docker/local.env down` |
| Stop and discard the logs | `docker compose --env-file docker/local.env down -v` |
| A shell in the container | `docker compose --env-file docker/local.env exec api sh` |

The env file has to be passed on **every** Compose command, not just `up`: it supplies
`COMPOSE_PROJECT_NAME`, so without it Compose looks for a differently-named project and reports
nothing running.

## WSL — the Development environment

Docker Engine installed **inside** the Ubuntu distro, against the PostgreSQL installed in that same
distro. This is the setup to prefer for Development: it is the only one where "runs inside the Ubuntu
distro" is literally true, and the only one where `DB_HOST` is a constant.

### 1. Confirm which engine the distro talks to

Inside the distro:

```bash
docker context ls
systemctl is-active docker
```

The context marked `*` should be `default` (`unix:///var/run/docker.sock`) and `docker` should be
active — that is the engine running in Ubuntu itself. If instead the active context is
`desktop-linux`, or `command -v docker` resolves into `/mnt/wsl/docker-desktop`, the distro is
borrowing Docker Desktop's engine; skip to [Using Docker Desktop's engine
instead](#using-docker-desktops-engine-instead).

### 2. Clear a credential helper left behind by Docker Desktop

If Docker Desktop's WSL integration was ever enabled, it wrote `~/.docker/config.json` in the distro
pointing at a **Windows** credential helper, which the native engine cannot execute:

```
docker: error getting credentials - err: fork/exec /mnt/c/Program Files/Docker/Docker/resources/bin/docker-credential-desktop.exe: exec format error
```

Every image pull fails on it, including the base images this build needs. Check and fix:

```bash
cat ~/.docker/config.json
```

If it contains `"credsStore": "desktop.exe"`, remove that entry (a file holding nothing else can
simply be deleted — anonymous pulls from Docker Hub need no credentials at all):

```bash
rm ~/.docker/config.json
```

### 3. Open PostgreSQL to the bridge network

This is the step that differs most from Windows, and the one that fails silently if skipped. The
container connects to `host.docker.internal`, which Compose maps to the bridge gateway
(`172.17.0.1`) — a **real** interface, not a forwarded loopback. Ubuntu's PostgreSQL ships listening
on `127.0.0.1` only, so it is unreachable from a container until told otherwise.

Check what it listens on:

```bash
ss -ltn | grep 5432
```

`127.0.0.1:5432` alone means the two edits below are both needed. In
`/etc/postgresql/<version>/main/postgresql.conf`:

```
listen_addresses = 'localhost,172.17.0.1'
```

Naming the gateway rather than `*` keeps the instance off every other interface — including the
distro's own address, which is visible from the Windows host and from the network beyond it.

Then, in `/etc/postgresql/<version>/main/pg_hba.conf`, a rule for the bridge range, above nothing and
below the loopback rules:

```
host    heimdall_development    heimdall_svc    172.16.0.0/12    scram-sha-256
```

`listen_addresses` requires a restart; `pg_hba.conf` alone would only need a reload:

```bash
sudo systemctl restart postgresql
```

### 4. Create the database and its login

```bash
sudo -u postgres createuser --pwprompt heimdall_svc
sudo -u postgres createdb --owner heimdall_svc heimdall_development
```

The warning about not naming the login `heimdall` applies here too.

### 5. Confirm the container can reach it

```bash
docker run --rm --add-host host.docker.internal:host-gateway alpine sh -c 'nc -z -w 3 host.docker.internal 5432 && echo reachable || echo unreachable'
docker run --rm --add-host host.docker.internal:host-gateway postgres:18-alpine psql "postgresql://heimdall_svc:<password>@host.docker.internal:5432/heimdall_development" -c "select 1"
```

`unreachable` points back at `listen_addresses`; `no pg_hba.conf entry for host` points at the
`pg_hba.conf` rule. Both are cheaper to diagnose here than from a container that will not start.

### 6. Where to keep the clone

A clone on a Windows drive is reachable from the distro at `/mnt/d/...`, and Compose will build from
there — but every file the build context reads crosses the 9p filesystem boundary, which makes the
build several times slower. For a deployment that is rebuilt often, clone into the distro's own
filesystem instead:

```bash
git clone --recurse-submodules https://github.com/artur-rios/heimdall-api.git ~/repos/heimdall-api
cd ~/repos/heimdall-api
```

### 7. Write the env file, build, start

```bash
cp docker/development.env.example docker/development.env
```

Fill in the same variables as the Local walkthrough, with `DB_NAME=heimdall_development`,
`DB_HOST=host.docker.internal`, and a signing secret of its own — a shared environment must not be
able to mint tokens the others accept. To generate one:

```bash
openssl rand -base64 48
```

Then:

```bash
docker compose --env-file docker/development.env up -d --build
docker compose --env-file docker/development.env ps
curl http://localhost:8080/healthcheck
```

The API answers on the distro's port 8080, and WSL forwards `localhost:8080` from Windows too, so a
browser on Windows reaches <http://localhost:8080/swagger> unchanged.

### Using Docker Desktop's engine instead

If the distro's `docker` is Docker Desktop's (WSL integration), the containers run in **Docker
Desktop's own VM**, not in Ubuntu — and `host.docker.internal` then points at the *Windows* host.
PostgreSQL inside the distro is reachable only at the distro's own address, which WSL reassigns on
restart:

```bash
DB_HOST=$(hostname -I | awk '{print $1}') docker compose --env-file docker/development.env up -d --build
```

A value from the shell wins over the one in the env file, so nothing has to be edited per boot. The
`listen_addresses` edit in step 3 must then name that address (or `*`), not the bridge gateway, and
the `pg_hba.conf` rule must cover Docker Desktop's range rather than `172.16.0.0/12`. This is the
setup to avoid if the native engine is an option.

## Production

Same commands, `docker/production.env`, and three differences that matter:

- `ASPNETCORE_ENVIRONMENT=Production` — no Swagger, no developer exception page, and the API
  **refuses to start** without `MAILGUN_API_KEY` and `MAILGUN_DOMAIN` rather than silently swallowing
  every e-mail it owes a user.
- `API_PORT=127.0.0.1:8080` — bound to loopback, for a reverse proxy to terminate TLS in front of.
  Publishing on every interface would expose the API directly, bypassing both the proxy and the
  firewall.
- `HEIMDALL_RUN_MIGRATIONS=false` if the environment ever runs more than one replica: two containers
  applying migrations at start-up will race each other. Apply them as their own deploy step with
  `python scripts/migrations.py`.

For a PostgreSQL on another machine in the VPC, put its private address in `DB_HOST` and require TLS
through `DB_CONNECTION_EXTRA=SSL Mode=Require;Trust Server Certificate=true` — dropping
`Trust Server Certificate` once the server presents a certificate the container can verify.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `set DB_USER in the env file` at `up` | Compose interpolation found the variable empty | Fill it in — the `:?` markers in the Compose file fail loudly rather than starting a container that cannot connect |
| Container restarts; log ends at `entrypoint: applying EF Core migrations...` | The bundle cannot reach PostgreSQL | Re-run the two probes above; the address in the error names which side is wrong |
| `no pg_hba.conf entry for host "..."` | The connection arrives from an address no rule covers | Add a `host` rule for that address, then `systemctl reload postgresql` |
| `relation "role" already exists` on the second run | The login is named `heimdall`, so `"$user"` resolves to the entities' schema | Rename the login, or keep `Search Path=public` in every connection string |
| `error getting credentials … docker-credential-desktop.exe` in WSL | `~/.docker/config.json` points at a Windows helper | Remove `credsStore` (step 2) |
| Browser front end gets a CORS error | `HEIMDALL_CORS_ALLOWED_ORIGINS` is empty | List the front end's origin exactly as the browser sends it |
| `docker compose ps` shows nothing | `--env-file` omitted, so `COMPOSE_PROJECT_NAME` is unset | Pass the env file on every Compose command |
| Health check never leaves `starting` | The API is up but `/healthcheck` is not answering | `logs -f api`; the start period is 30 s, five retries at 15 s after that |

## Next

- [Operations](../operations/) — migrations, start-up guards, health checks, logging, integrations.
- [Getting started](../getting-started/) — running the API from source instead.
