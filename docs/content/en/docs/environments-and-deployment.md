+++
title = 'Environments and deployment'
linkTitle = 'Environments and deployment'
weight = 75
description = 'The four environments — local on Docker Desktop, development, homologation and production on one VPS through yggdrasil — and, step by step, what the PostgreSQL behind each has to allow.'
aliases = ['/docs/deploying-with-docker/']
+++

The API runs in four environments. All of them use the same image and the same
[`docker-compose.yml`](https://github.com/artur-rios/heimdall-api/blob/main/docker-compose.yml);
what differs between them is the env file, who deploys it and, mostly, what the host's PostgreSQL
has to be told to allow.

| Environment | Where | Deployed by | `ASPNETCORE_ENVIRONMENT` | API at | Database |
| --- | --- | --- | --- | --- | --- |
| `local` | The developer's Windows machine, Docker Desktop | By hand, `docker compose --env-file docker/local.env up -d --build` | `Development` | `http://localhost:8080` | `heimdall_local`, PostgreSQL on Windows |
| `development` | The VPS, **on demand** | Jenkins, on every push to `develop` | `Development` | `https://heimdall-api-dev.example.com` | `heimdall_development`, PostgreSQL on the VPS |
| `homologation` | The VPS, **on demand** | Jenkins, on every push of a `release/x.y.z` branch | `Staging` | `https://heimdall-api-hml.example.com` | `heimdall_homologation`, PostgreSQL on the VPS |
| `production` | The VPS, always on | Jenkins, on a green `release/x.y.z → main` pull request — it then merges and tags | `Production` | `https://heimdall-api.example.com` | `heimdall`, PostgreSQL on the VPS |

`example.com` stands for the real domain throughout this page. In every VPS environment the API is
also served under the web UI's own host at `/api/` (`https://heimdall-dev.example.com/api/…`,
`-hml`, and `https://heimdall.example.com/api/…`), which is how heimdall-ui's browser build calls
it without CORS.

What the ASP.NET environment changes:

| | `Development` (local, development) | `Staging` (homologation) | `Production` |
| --- | --- | --- | --- |
| Swagger UI | Served | Not served | Not served |
| Developer exception page | On | Off | Off |
| EF Core parameter logging | On | On | Off |
| Verification and reset e-mails without Mailgun | Logged instead of sent | Logged instead of sent | **Start-up fails** |

**Local** is deployed by hand on Docker Desktop. **Development, homologation and production** share
one Ubuntu VPS and are deployed by [yggdrasil](https://github.com/artur-rios/yggdrasil), whose Jenkins
runs this repository's
[`Jenkinsfile`](https://github.com/artur-rios/heimdall-api/blob/main/Jenkinsfile). Development and
homologation are started only while somebody uses them.

## What is being deployed

One service, `api`, built from the repository's [`Dockerfile`](https://github.com/artur-rios/heimdall-api/blob/main/Dockerfile):

- A multi-stage build that publishes the Web API **and** an EF Core migrations bundle. The bundle is
  a plain executable, so the runtime image never needs the SDK or `dotnet-ef`.
- The entrypoint applies pending migrations and only then starts the API — which is what lets a
  container be pointed at an empty database. Set `HEIMDALL_RUN_MIGRATIONS=false` to apply them out of
  band instead; see [Operations](../operations/#migrations).
- The container drops to a non-root user, writes logs to a named volume, and answers a health check
  on `/healthcheck` — Compose reports the container healthy only once the API answers.

**PostgreSQL is deliberately not a service in the Compose file.** Every host already runs one
instance shared by several services and environments, each owning a database of its own. So the
first half of each walkthrough below is about that instance, not about Docker.

{{% alert title="Three environments, one engine" color="info" %}}
The VPS runs development, homologation and production on **one Docker engine and one PostgreSQL
instance**. They are kept apart by name, not by machine: each environment's Compose project is
`heimdall-api-<environment>`, its image tags start with `<environment>-`, its network alias is
`heimdall-api.<environment>`, and its database and login are its own. Nothing an environment is
given — secret, master user, database — is shared with another.
{{% /alert %}}

## Windows — the local environment

Docker Desktop on the developer's machine, against the PostgreSQL installed on Windows. Nothing
deploys here but the developer: no Jenkins, no Traefik, the port published on the host.

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
`ASPNETCORE_ENVIRONMENT=Development` and the Mailgun variables are empty. The e-mails' links point
at the local heimdall-ui, `http://localhost:8081`.

{{% alert title="CORS lists the local heimdall-ui" color="info" %}}
`HEIMDALL_CORS_ALLOWED_ORIGINS` unset refuses every cross-origin request, which is the right default
for an API that hands out credentials. Locally heimdall-ui is served from `http://localhost:8081` and
calls the API on `http://localhost:8080` — another origin — so the template lists it. Any other
browser front end needs its origin added, scheme, host and port exactly as the browser sends them.
On the VPS no entry is needed: heimdall-ui calls the API under its own host.
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

## The VPS — development, homologation and production

One Ubuntu VPS runs all three, behind yggdrasil's Traefik, against the PostgreSQL installed on the
VPS itself. The VPS's own setup — Docker, the platform stack, Jenkins, the wildcard certificate and
the single `*.example.com` DNS record that covers `heimdall-api-dev`, `heimdall-api-hml` and
`heimdall-api` alike — is yggdrasil's, and described in its documentation. What follows is what this
API needs on top of it.

### How yggdrasil deploys it

Jenkins builds the image on the VPS and runs `docker compose` with this repository's Compose file
**plus** yggdrasil's `stacks/heimdall-api.proxy.yml`, which:

- removes the host port — Traefik, on the shared `edge` network, is the only way in, so `API_PORT`
  means nothing here;
- names the project `heimdall-api-<environment>` (`COMPOSE_PROJECT_NAME` is ignored too) and gives
  the container the alias `heimdall-api.<environment>` on the `edge` and `telemetry` networks —
  Prometheus scrapes `heimdall-api.<environment>:9464` there;
- routes two hosts to it: `PUBLIC_HOST` (`heimdall-api-dev.example.com`) for the mobile and desktop
  clients, and `UI_HOST` + `/api/` (`heimdall-dev.example.com/api/`) for heimdall-ui's browser build.

The env file is `/etc/yggdrasil/<environment>/heimdall-api.env` on the VPS, never in a repository.

| Push | Deploys to | Afterwards |
| --- | --- | --- |
| `develop` | development | Left stopped if it was stopped |
| `release/x.y.z` | homologation | Left stopped if it was stopped |
| Pull request `release/x.y.z → main`, every check green | production | Jenkins merges the pull request and tags `vx.y.z` |

**Development and homologation are on demand.** A deploy still builds, starts and health-checks the
new version — a broken build is caught and rolled back either way — but an environment that was not
running before the deploy is stopped again afterwards. To use one, on the VPS:

```bash
scripts/ygg.sh env start development
```

and `scripts/ygg.sh env stop development` when done (`homologation` likewise). The yggdrasil console
shows each environment's state, a stopped on-demand environment as *Stopped* rather than as a
problem.

### 1. Open PostgreSQL to the bridge network

This is the step that fails silently if skipped. The container connects to `host.docker.internal`,
which Compose maps to the Docker bridge gateway (`172.17.0.1`) — a **real** interface, not a
forwarded loopback. Ubuntu's PostgreSQL ships listening on `127.0.0.1` only, so it is unreachable
from a container until told otherwise.

Check what it listens on:

```bash
ss -ltn | grep 5432
```

`127.0.0.1:5432` alone means the edit below is needed. In
`/etc/postgresql/<version>/main/postgresql.conf`:

```
listen_addresses = 'localhost,172.17.0.1'
```

Naming the gateway rather than `*` keeps the instance off every other interface — including the
VPS's public address. `listen_addresses` requires a restart:

```bash
sudo systemctl restart postgresql
```

### 2. Create each environment's database and login

One PostgreSQL instance, one database **and one login** per environment, so that a development or
homologation container — or a leaked development password — cannot reach production's data.
Production keeps the database it has always had, `heimdall`:

```bash
sudo -u postgres createuser --pwprompt heimdall_svc
sudo -u postgres createdb --owner heimdall_svc heimdall

sudo -u postgres createuser --pwprompt heimdall_development_svc
sudo -u postgres createdb --owner heimdall_development_svc heimdall_development

sudo -u postgres createuser --pwprompt heimdall_homologation_svc
sudo -u postgres createdb --owner heimdall_homologation_svc heimdall_homologation
```

Skip the pair production already has. Then, in `/etc/postgresql/<version>/main/pg_hba.conf`, one rule
per pair for the Docker range, below the loopback rules — each login reaches its own database and no
other:

```
host    heimdall                 heimdall_svc                 172.16.0.0/12    scram-sha-256
host    heimdall_development     heimdall_development_svc     172.16.0.0/12    scram-sha-256
host    heimdall_homologation    heimdall_homologation_svc    172.16.0.0/12    scram-sha-256
```

`172.16.0.0/12` rather than one network's subnet: yggdrasil's `edge` and `telemetry` networks, and
any network Docker creates later, are all allocated from it. `pg_hba.conf` needs only a reload:

```bash
sudo systemctl reload postgresql
```

{{% alert title="Do not name a login heimdall" color="warning" %}}
The same trap as on Windows: a login named `heimdall` turns the search path's `"$user"` into the
entities' schema. Compose pins `Search Path=public`, but the safest thing is still a login called
something else.
{{% /alert %}}

### 3. Confirm a container can reach each database

```bash
docker run --rm --add-host host.docker.internal:host-gateway alpine sh -c 'nc -z -w 3 host.docker.internal 5432 && echo reachable || echo unreachable'
docker run --rm --add-host host.docker.internal:host-gateway postgres:18-alpine psql "postgresql://heimdall_development_svc:<password>@host.docker.internal:5432/heimdall_development" -c "select 1"
```

Repeat the second line for homologation and production. `unreachable` points back at
`listen_addresses`; `no pg_hba.conf entry for host` points at the `pg_hba.conf` rules. And the
separation should hold — the development login against production's database must be refused:

```bash
docker run --rm --add-host host.docker.internal:host-gateway postgres:18-alpine psql "postgresql://heimdall_development_svc:<password>@host.docker.internal:5432/heimdall" -c "select 1"
```

### 4. Write each environment's env file

Each environment has a template in the repository — `docker/development.env.example`,
`docker/homologation.env.example`, `docker/production.env.example` — already filled in with what
that environment needs, `example.com` standing for the domain. On the VPS, as the user that owns
yggdrasil's secrets directory:

```bash
cp docker/development.env.example /etc/yggdrasil/development/heimdall-api.env
```

Replace `example.com` with the real domain, then fill in at least:

| Variable | Value |
| --- | --- |
| `DB_USER` / `DB_PASSWORD` | That environment's login from step 2 |
| `HEIMDALL_AUTH_TOKEN_SECRET` | A long random value of its own (`openssl rand -base64 48`) — fortuna-api in the same environment is given the same value |
| `HEIMDALL_MASTER_USER_NAME` / `_EMAIL` / `_PASSWORD` | That environment's first system administrator, seeded on the first run against its empty database |
| `MAILGUN_API_KEY` / `MAILGUN_DOMAIN` | Production only — it refuses to start without them |

What the templates already set, and how the three differ:

| Variable | development | homologation | production |
| --- | --- | --- | --- |
| `PUBLIC_HOST` | `heimdall-api-dev.example.com` | `heimdall-api-hml.example.com` | `heimdall-api.example.com` |
| `UI_HOST` | `heimdall-dev.example.com` | `heimdall-hml.example.com` | `heimdall.example.com` |
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Staging` | `Production` |
| `DB_NAME` | `heimdall_development` | `heimdall_homologation` | `heimdall` |
| `HEIMDALL_EMAIL_VERIFICATION_URL` | `https://heimdall-dev.example.com/verify-email` | `https://heimdall-hml.example.com/verify-email` | `https://heimdall.example.com/verify-email` |
| `HEIMDALL_PASSWORD_RESET_URL` | `https://heimdall-dev.example.com/password-reset` | `https://heimdall-hml.example.com/password-reset` | `https://heimdall.example.com/password-reset` |

All three set `DB_HOST=host.docker.internal` and `HEIMDALL_TRUSTED_PROXIES=172.16.0.0/12` — Traefik
reaches the container from yggdrasil's `edge` network, so forwarded addresses are believed from the
Docker range and from nobody else — and leave `HEIMDALL_CORS_ALLOWED_ORIGINS` empty, since
heimdall-ui calls the API under its own origin.

`scripts/ygg.sh config heimdall-api` on the VPS opens the file of the environment it asks for and
redeploys with it.

{{% alert title="Development and homologation log personal data" color="warning" %}}
Neither sends e-mail without Mailgun: the verification and reset tokens — and 2FA codes — are written
to the log instead, and EF Core logs parameter values outside Production. That is what lets them
run without a mail account, and it is why they are for made-up people: never load production's data
into them. Development also serves Swagger and the developer exception page to anyone who reaches
its host — one more reason to keep it stopped while nobody uses it.
{{% /alert %}}

### 5. Deploy and verify

Push — `develop` for development, a `release/x.y.z` branch for homologation — and follow the build in
Jenkins or in the yggdrasil console. The first deploy of an on-demand environment ends stopped; start
it, then:

```bash
curl https://heimdall-api-dev.example.com/healthcheck
curl https://heimdall-dev.example.com/api/healthcheck
```

Both answer when Traefik routes both hosts. The start-up log should show the migrations applied and
then the API listening:

```bash
docker compose -p heimdall-api-development logs -f api
```

### 6. Day-to-day

| Task | Command, on the VPS |
| --- | --- |
| Turn development or homologation on / off | `scripts/ygg.sh env start development` / `scripts/ygg.sh env stop development` |
| What runs in each environment | `scripts/ygg.sh env status` |
| Follow the logs | `docker compose -p heimdall-api-<environment> logs -f api` |
| Change the env file and redeploy | `scripts/ygg.sh config heimdall-api` |
| A shell in the container | `docker compose -p heimdall-api-<environment> exec api sh` |

Redeploys come from Jenkins, not from `docker compose up` by hand: a hand-run `up` would skip
yggdrasil's overlay and publish the API's port on the host.

## Production

Production follows the steps above with `docker/production.env.example`, and differs in what matters:

- `ASPNETCORE_ENVIRONMENT=Production` — no Swagger, no developer exception page, no EF parameter
  logging, and the API **refuses to start** without `MAILGUN_API_KEY` and `MAILGUN_DOMAIN` rather
  than silently swallowing every e-mail it owes a user.
- It is always on: `scripts/ygg.sh env stop production` refuses unless forced.
- It is deployed only from a release pull request whose checks have all passed, and a failed deploy
  rolls back to the previous production image and leaves the pull request open.
- `HEIMDALL_RUN_MIGRATIONS=false` if it ever runs more than one replica: two containers applying
  migrations at start-up will race each other. Apply them as their own deploy step with
  `python scripts/migrations.py`.

For a PostgreSQL on another machine, put its private address in `DB_HOST` and require TLS through
`DB_CONNECTION_EXTRA=SSL Mode=Require;Trust Server Certificate=true` — dropping
`Trust Server Certificate` once the server presents a certificate the container can verify.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `set DB_USER in the env file` at `up` | Compose interpolation found the variable empty | Fill it in — the `:?` markers in the Compose file fail loudly rather than starting a container that cannot connect |
| Container restarts; log ends at `entrypoint: applying EF Core migrations...` | The bundle cannot reach PostgreSQL | Re-run the two probes above; the address in the error names which side is wrong |
| `no pg_hba.conf entry for host "..."` | The connection arrives from an address no rule covers | Add a `host` rule for that address, then `systemctl reload postgresql` |
| `relation "role" already exists` on the second run | The login is named `heimdall`, so `"$user"` resolves to the entities' schema | Rename the login, or keep `Search Path=public` in every connection string |
| Browser front end gets a CORS error | `HEIMDALL_CORS_ALLOWED_ORIGINS` is empty | List the front end's origin exactly as the browser sends it |
| Every request is logged from the same address, and callers hit 429 together | `HEIMDALL_TRUSTED_PROXIES` does not cover the proxy | Set it to the address or network the proxy connects from (Operations → Client addresses behind a proxy) |
| `docker compose ps` shows nothing (local) | `--env-file` omitted, so `COMPOSE_PROJECT_NAME` is unset | Pass the env file on every Compose command |
| `docker compose ps` shows nothing (VPS) | The project is named after the environment | `docker compose -p heimdall-api-<environment> ps` |
| `heimdall-api-dev.example.com` answers 404 | Development is stopped, so Traefik has no router for it | `scripts/ygg.sh env start development` |
| Health check never leaves `starting` | The API is up but `/healthcheck` is not answering | `logs -f api`; the start period is 30 s, five retries at 15 s after that |

## Next

- [Operations](../operations/) — migrations, start-up guards, health checks, logging, integrations.
- [Getting started](../getting-started/) — running the API from source instead.
- [Contributing → Releasing](../contributing/#releasing) — how a release reaches homologation and
  production.
