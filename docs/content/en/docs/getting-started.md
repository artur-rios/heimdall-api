+++
title = 'Getting started'
linkTitle = 'Getting started'
weight = 20
description = 'Prerequisites, configuration, migrations, and running the API locally.'
+++

## Prerequisites

- **.NET 10 SDK**
- **PostgreSQL** — the API's relational database. (Functional tests spin up their own instance via
  Testcontainers, so a running PostgreSQL is only needed to run the API itself.)
- **Docker** — required by Testcontainers when running the functional suite.
- **Python 3** — only to run the migration menu script.
- The pinned EF Core CLI tool. Restore it once after cloning:

```bash
dotnet tool restore
```

## Clone

The documentation site's theme is a git submodule, so clone with submodules (or initialise them
afterwards):

```bash
git clone --recurse-submodules https://github.com/artur-rios/heimdall-api.git
```

## Configure

`Environments/.env` is a **tracked template** listing every variable the API reads. Real values live
in per-environment files that are gitignored. Create your local one before the first run:

```bash
cp src/Presentation/ArturRios.Heimdall.WebApi/Environments/.env src/Presentation/ArturRios.Heimdall.WebApi/Environments/.env.local
```

### Required — the API will not start without these

| Variable | Value |
| --- | --- |
| `HEIMDALL_DATA_CONNECTIONSTRING` | A PostgreSQL connection string |
| `HEIMDALL_DATA_DATABASETYPE` | `PostgreSql` |
| `HEIMDALL_AUTH_TOKEN_SECRET` | The JWT signing secret. Startup throws if unset — otherwise every authenticated request would fail inside the token validator with an opaque `IDX10703` |
| `HEIMDALL_MASTER_USER_NAME` / `_EMAIL` / `_PASSWORD` | Credentials for the first system administrator. Consulted only when the database holds no system admin yet, but the seeder refuses to start without them in that case |

### Optional — each falls back to a default

| Variable | Default |
| --- | --- |
| `HEIMDALL_AUTH_TOKEN_ISSUER` / `_AUDIENCE` | Empty |
| `HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS` | `3600` (1 hour) |
| `HEIMDALL_EMAIL_VERIFICATION_TOKEN_EXPIRATION_IN_SECONDS` | `86400` (24 hours) |
| `HEIMDALL_PASSWORD_RESET_TOKEN_EXPIRATION_IN_SECONDS` | `3600` (1 hour) |
| `HEIMDALL_LOG_DIRECTORY` | `logs` |

### Optional integrations

Email delivery (Mailgun) and Google Sign-In are each configured by their own variables, and each has
a documented behaviour when left unconfigured. See [Operations](../operations/#integrations) for the
full table and the reasoning — including why an unconfigured **Production** deployment refuses to
start rather than logging tokens in plaintext.

## Apply migrations

The schema is managed with **EF Core migrations, applied explicitly**. The API never migrates on
startup, and **refuses to start when migrations are pending** — so this step comes before the first
run:

```bash
python scripts/migrations.py
```

The script asks which environment file to load (for the connection string), then offers *list*,
*create*, and *apply*. See [Operations](../operations/#migrations) for more.

## Build

```bash
dotnet build src/ArturRios.Heimdall.sln
```

## Run

```bash
dotnet run --project src/Presentation/ArturRios.Heimdall.WebApi
```

On the first run against an empty database, the seeder creates the roles and the master system
administrator from `HEIMDALL_MASTER_USER_NAME` / `_EMAIL` / `_PASSWORD`. On later runs, an existing
system admin means those variables are read but unused.

Swagger UI is generated with JWT authentication wired in, so you can paste a bearer token and call
the endpoints from the browser.

## First calls

Liveness — anonymous, no database:

```bash
curl http://localhost:5000/healthcheck
```

Log in as the master system administrator:

```bash
curl -X POST http://localhost:5000/api/auth/login -H "Content-Type: application/json" -d '{"email":"<master-email>","password":"<master-password>"}'
```

The response carries `token` and `expiresAt`. Send it as `Authorization: Bearer <token>` on every
other endpoint — see the [API reference](../api-reference/) for who may call what.

{{% alert title="Two-factor" color="info" %}}
If the person has active 2FA, login answers `requiresTwoFactor: true` with a short-lived
`challengeToken` instead of a full token. Finish the login at `POST /api/auth/2fa/verify` — see the
[two-factor flow](../flows/two-factor/).
{{% /alert %}}

## Next

- [Testing](../testing/) — how to run the suites.
- [Architecture](../architecture/) — what happens between the controller and the database.
- [Development Workflow](../requirements/development-workflow-document/) — how a use case goes from
  backlog to merged.
