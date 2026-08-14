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

The `http` launch profile serves on `http://localhost:5177`; the `https` profile adds
`https://localhost:7235`.

**Swagger UI** is at <http://localhost:5177/swagger>, with JWT wired in: press **Authorize**, paste
the token from a login, and call any endpoint from the browser. It shows each controller's own
summary and marks which endpoints need a token, because `SwaggerConfiguration` is applied over it.

That same class produces the document behind the [API explorer](../api-explorer/), so the two are the
same document — literally: `python scripts/openapi.py` writes byte-for-byte what a running instance
serves at `/swagger/v1/swagger.json`. Read the API without running it through the explorer; call it
through Swagger UI or `api-client/`.

{{% alert title="Why Swagger sits between the two middlewares" color="info" %}}
`Startup.Build()` registers `ExceptionMiddleware`, then `UseSwagger()`, then
`AuthenticationMiddleware` — not both middlewares before Swagger. Authentication does not exempt the
Swagger routes, so registering it first answered 401 to every request for `/swagger`, `index.html`
included, which no browser can satisfy. Keeping the exception middleware ahead means a failure inside
Swagger still answers the usual JSON envelope.

Swagger is registered only in the environments the library allows, and in Production it registers
nothing at all — so this does not publish the document from a production instance.
{{% /alert %}}

## First calls

Liveness — anonymous, no database:

```bash
curl http://localhost:5177/healthcheck
```

Log in as the master system administrator:

```bash
curl -X POST http://localhost:5177/api/auth/login -H "Content-Type: application/json" -d '{"email":"<master-email>","password":"<master-password>"}'
```

The response carries `token`, `expiresAt`, and `emailVerified` — the last telling you whether to
prompt the person to confirm their address. Send it as `Authorization: Bearer <token>` on every
other endpoint — see the [API reference](../api-reference/) for who may call what.

{{% alert title="Two-factor" color="info" %}}
If the person has active 2FA, login answers `requiresTwoFactor: true` with a short-lived
`challengeToken` instead of a full token. Finish the login at `POST /api/auth/2fa/verify` — see the
[two-factor flow](../flows/two-factor/).
{{% /alert %}}

## Calling the API

`api-client/` holds ready-to-send requests for all 49 endpoints, in two formats — pick whichever your
editor already opens:

| Directory | Format | Opens in |
| --- | --- | --- |
| `api-client/http/` | JetBrains HTTP Client (`.http`) | Rider, IntelliJ IDEA, VS Code with the REST Client extension |
| `api-client/bruno/` | [Bruno](https://www.usebruno.com) collection | The Bruno app, or `bru run` on the command line |

Neither asks you to copy a GUID by hand: a response handler stores the token that login returns, and
each request that creates something stores the new id for the requests that need it. Point the
environment file at your master user's credentials first — `http/http-client.env.json` or
`bruno/environments/Local.bru`.

On an empty database four requests have to run in order before anything else will find its subject:
**Login**, then **Create an administrator**, then **Create a scope**, then **Create a User in a
scope**. See [`api-client/README.md`](https://github.com/artur-rios/heimdall-api/blob/main/api-client/README.md)
for the whole story, including how to keep real credentials out of git.

To run the Bruno collection without installing anything:

```bash
npx @usebruno/cli run Auth --env Local
```

## Next

- [Testing](../testing/) — how to run the suites.
- [Architecture](../architecture/) — what happens between the controller and the database.
- [Development Workflow](../requirements/development-workflow-document/) — how a use case goes from
  backlog to merged.
