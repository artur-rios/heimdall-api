# ArturRios.Heimdall

A centralized **identity management API** built with ASP.NET Core (.NET 10). It provides person
management, application (non-human identity) management, authentication and authorization,
password recovery, email verification, and Google Sign-In for multiple client systems through
**scope-based multi-tenancy** — each client system operates within its own isolated scope.

## Overview

- **Multi-tenant by scope.** Every `User` belongs to exactly one scope; a `ScopeAdmin` owns one or
  more scopes; a `SystemAdmin` governs the whole system and belongs to no scope.
- **Persons & applications.** Manages human identities (persons) and non-human identities
  (applications, each owned by a Scope Admin who owns the application's scope).
- **Scope-specific permissions.** Each scope can define its own permissions, and a permission
  flagged `IncludeAsJwtClaim` is folded into the JWT issued to identities acting within that scope.
- **Authentication.** Password-based login (JWT), password recovery, email verification, optional
  Google Sign-In per scope, and optional two-factor authentication (authenticator app and/or email,
  with single-use recovery codes).
- **Deletion strategies.** Both logical (soft) and hard deletion, with well-defined cascade rules.
- **Layered (DDD) architecture.** Domain, Application (CQRS: Command/Query/Shared), Infrastructure
  (EF Core data layer), and Presentation (Web API).

For the full picture — vision, requirements, use cases, the technology stack, and testing standards —
see [Documentation](#documentation).

## Project structure

```
docs/requirements/                             Vision, requirements, use cases, tech stack, testing
scripts/                                        Tooling (EF Core migration menu)
src/
  Domain/ArturRios.Heimdall.Domain/      Domain entities & enums
  Application/
    ArturRios.Heimdall.Command/          Command (write) handlers — CQRS
    ArturRios.Heimdall.Query/            Query (read) handlers — CQRS
    ArturRios.Heimdall.Shared/           Shared messages/contracts
  Infrastructure/ArturRios.Heimdall.Data/ EF Core DbContext, entity maps, migrations, seeding
  Presentation/ArturRios.Heimdall.WebApi/ ASP.NET Core Web API (entry point)
  ArturRios.Heimdall.sln
tests/                                          Test projects mirroring src/ (unit + functional)
README.md
LICENSE
```

## Documentation

Detailed documentation lives under [`docs/requirements`](docs/requirements):

- [Vision Document](docs/requirements/Vision%20Document.md) — product vision, stakeholders, and goals.
- [System Requirements Document](docs/requirements/System%20Requirements%20Document.md) — functional
  and non-functional requirements, data model, endpoints, and authorization matrix.
- [Use Case Specification Document](docs/requirements/Use%20Case%20Specification%20Document.md) —
  the use cases with flows and alternative flows.
- [Operations & Infrastructure Document](docs/requirements/Operations%20%26%20Infrastructure%20Document.md) —
  technical foundation and the health-check feature.
- [Technology Stack Document](docs/requirements/Technology%20Stack%20Document.md) — the technologies,
  libraries, and versions the project is built on.
- [Testing Specification Document](docs/requirements/Testing%20Specification%20Document.md) — how each
  use case is tested (unit + functional standards).
- [Development Workflow Document](docs/requirements/Development%20Workflow%20Document.md) — how a use
  case goes from backlog to merged (branch, issue status, testing gate, PR).

## Use case status

Delivery tracker for the use cases in the
[Use Case Specification Document](docs/requirements/Use%20Case%20Specification%20Document.md), plus
the platform work that is not itself a use case. Each one ships on its own branch, issue, and pull
request — see the
[Development Workflow Document](docs/requirements/Development%20Workflow%20Document.md).

**Legend:** ✅ done and merged &nbsp;·&nbsp; 🚧 in progress &nbsp;·&nbsp; ⬜ not started

### Scope Management

| Use case | Status | Issue |
| --- | --- | --- |
| UC-01: Create Scope | ✅ | [#2](https://github.com/artur-rios/heimdall-api/issues/2) |
| UC-02: View Scope | ✅ | [#3](https://github.com/artur-rios/heimdall-api/issues/3) |
| UC-03: Update Scope | ✅ | [#4](https://github.com/artur-rios/heimdall-api/issues/4) |
| UC-04: Logical Delete Scope | ✅ | [#5](https://github.com/artur-rios/heimdall-api/issues/5) |
| UC-05: Hard Delete Scope | ✅ | [#6](https://github.com/artur-rios/heimdall-api/issues/6) |
| UC-21: Add Scope Owner | ✅ | [#22](https://github.com/artur-rios/heimdall-api/issues/22) |
| UC-22: Remove Scope Owner | ✅ | [#23](https://github.com/artur-rios/heimdall-api/issues/23) |
| UC-23: Promote User to Scope Owner | ✅ | [#24](https://github.com/artur-rios/heimdall-api/issues/24) |

### Person Management

| Use case | Status | Issue |
| --- | --- | --- |
| UC-06: Create Person | ✅ | [#7](https://github.com/artur-rios/heimdall-api/issues/7) |
| UC-07: View Person | ✅ | [#8](https://github.com/artur-rios/heimdall-api/issues/8) |
| UC-08: Update Person | ✅ | [#9](https://github.com/artur-rios/heimdall-api/issues/9) |
| UC-09: Logical Delete Person | ✅ | [#10](https://github.com/artur-rios/heimdall-api/issues/10) |
| UC-10: Hard Delete Person | ✅ | [#11](https://github.com/artur-rios/heimdall-api/issues/11) |

### Authentication & Security

| Use case | Status | Issue |
| --- | --- | --- |
| UC-11: Login (Authenticate) | ✅ | [#12](https://github.com/artur-rios/heimdall-api/issues/12) |
| UC-12: Request Password Recovery | ✅ | [#13](https://github.com/artur-rios/heimdall-api/issues/13) |
| UC-13: Reset Password | ✅ | [#14](https://github.com/artur-rios/heimdall-api/issues/14) |
| UC-14: Verify Email | ✅ | [#15](https://github.com/artur-rios/heimdall-api/issues/15) |
| UC-15: Resend Verification Email | ✅ | [#16](https://github.com/artur-rios/heimdall-api/issues/16) |
| UC-36: Enable Two-Factor Authentication | ✅ | [#68](https://github.com/artur-rios/heimdall-api/issues/68) |
| UC-37: Confirm Two-Factor Authentication Setup | ⬜ | — |
| UC-38: Verify Second Factor | ⬜ | — |
| UC-39: Disable Two-Factor Authentication | ⬜ | — |
| UC-40: Regenerate Recovery Codes | ⬜ | — |

### Application Management

| Use case | Status | Issue |
| --- | --- | --- |
| UC-16: Create Application | ✅ | [#17](https://github.com/artur-rios/heimdall-api/issues/17) |
| UC-17: View Application | ✅ | [#18](https://github.com/artur-rios/heimdall-api/issues/18) |
| UC-18: Update Application | ✅ | [#19](https://github.com/artur-rios/heimdall-api/issues/19) |
| UC-19: Logical Delete Application | ✅ | [#20](https://github.com/artur-rios/heimdall-api/issues/20) |
| UC-20: Hard Delete Application | ✅ | [#21](https://github.com/artur-rios/heimdall-api/issues/21) |

### Scope Permission Management

| Use case | Status | Issue |
| --- | --- | --- |
| UC-31: Create Scope Permission | ✅ | — |
| UC-32: View Scope Permission | ✅ | — |
| UC-33: Update Scope Permission | ✅ | — |
| UC-34: Logical Delete Scope Permission | ✅ | — |
| UC-35: Hard Delete Scope Permission | ✅ | — |

> These five were delivered together on one branch rather than one branch, issue, and pull request
> each, so they carry no issue link. See the
> [Development Workflow Document](docs/requirements/Development%20Workflow%20Document.md) for the
> standard the rest of the tracker follows.

### Google Sign-In

| Use case | Status | Issue |
| --- | --- | --- |
| UC-24: Enable/Disable Google Sign-In | ✅ | [#25](https://github.com/artur-rios/heimdall-api/issues/25) |
| UC-25: Sign Up / Sign In via Google | ✅ | [#26](https://github.com/artur-rios/heimdall-api/issues/26) |
| UC-26: Sign Out via Google | ✅ | [#27](https://github.com/artur-rios/heimdall-api/issues/27) |
| UC-27: View Google User | ✅ | [#28](https://github.com/artur-rios/heimdall-api/issues/28) |
| UC-28: Logical Delete Google User | ✅ | [#29](https://github.com/artur-rios/heimdall-api/issues/29) |
| UC-29: Hard Delete Google User | ✅ | [#30](https://github.com/artur-rios/heimdall-api/issues/30) |

### Platform

Not use cases, tracked separately.

| Item | Status | Issue |
| --- | --- | --- |
| Project scaffolding & initial infrastructure | ✅ | [#31](https://github.com/artur-rios/heimdall-api/issues/31) |
| Health check (liveness + detailed dependency check, UC-30) | ✅ | [#32](https://github.com/artur-rios/heimdall-api/issues/32) |
| Audit logging for write operations (NFR-09) | ⬜ | — |
| Real email delivery (Mailgun, via `ArturRios.Messaging`) | ✅ | — |

One cross-cutting requirement is deliberately outstanding rather than forgotten:

- **NFR-09 (audit logging).** Write handlers currently produce no audit entries; the Serilog setup
  covers request/startup logging only. Every use case merged so far ships without it, so it is
  tracked here as one platform item rather than being retro-fitted per use case.

**Email delivery** closed with UC-12: both the verification and password reset emails now go through
Mailgun (see [Email delivery](#email-delivery)). The logging senders survive as the fallback for
environments without credentials, which is what keeps the functional suite off the network.

## Prerequisites

- **.NET 10 SDK**
- **PostgreSQL** (the API's relational database; functional tests spin up their own via Testcontainers)
- **Python 3** (only to run the migration menu script)
- The pinned EF Core CLI tool — restore it once after cloning:

  ```bash
  dotnet tool restore
  ```

## Configure

`Environments/.env` is a tracked template listing every variable the API reads. Real values live in
per-environment files that are gitignored. Create your local one before the first run:

```bash
cp src/Presentation/ArturRios.Heimdall.WebApi/Environments/.env src/Presentation/ArturRios.Heimdall.WebApi/Environments/.env.local
```

Then fill it in. These are **required** — the API fails to start without them:

| Variable | Value |
| --- | --- |
| `HEIMDALL_DATA_CONNECTIONSTRING` | A PostgreSQL connection string |
| `HEIMDALL_DATA_DATABASETYPE` | `PostgreSql` |
| `HEIMDALL_AUTH_TOKEN_SECRET` | The JWT signing secret. Startup throws if it is unset — without it every authenticated request would fail inside the token validator with an opaque `IDX10703` |
| `HEIMDALL_MASTER_USER_NAME` / `_EMAIL` / `_PASSWORD` | Credentials for the first system administrator. Only consulted when the database holds no system admin yet, but the seeder refuses to start without them in that case |

The rest are optional and fall back to a default:

| Variable | Default |
| --- | --- |
| `HEIMDALL_AUTH_TOKEN_ISSUER` / `_AUDIENCE` | Empty |
| `HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS` | `3600` (1 hour) |
| `HEIMDALL_EMAIL_VERIFICATION_TOKEN_EXPIRATION_IN_SECONDS` | `86400` (24 hours) |
| `HEIMDALL_PASSWORD_RESET_TOKEN_EXPIRATION_IN_SECONDS` | `3600` (1 hour) |
| `HEIMDALL_LOG_DIRECTORY` | `logs` |

### Email delivery

Verification (UC-06) and password reset (UC-12) emails go out through Mailgun, using
[`ArturRios.Messaging`](https://github.com/artur-rios/dotnet-messaging). Delivery is enabled only
when **both** credentials are present:

| Variable | Value |
| --- | --- |
| `MAILGUN_API_KEY` | Your Mailgun private API key |
| `MAILGUN_DOMAIN` | The Mailgun sending domain |
| `MAILGUN_API_VERSION` | Optional; defaults to `v3` |

Leave them unset — as local runs and the functional test suite do — and the API logs each token
instead of emailing it, warning once at start-up. That is a supported mode, not a broken one: it
keeps the API runnable without credentials and keeps the test suite off the network.

Both emails carry a link built from these, with the token appended as a `token` query parameter. If
a link is not configured the email carries the bare token instead, which still works — the link is
only a convenience wrapper around it.

| Variable | Value |
| --- | --- |
| `HEIMDALL_EMAIL_VERIFICATION_URL` | Front-end page that verifies an email address |
| `HEIMDALL_PASSWORD_RESET_URL` | Front-end page that sets a new password |

Each page finishes the job by posting its token back: the verification page to
`POST /api/auth/verify-email` (UC-14), the reset page to `POST /api/auth/password-reset` (UC-13),
along with the new password. Both endpoints are anonymous, since someone arriving from a link in
their mail client holds no token of any other kind.

Spending a token of either kind retires every other live token the person holds of that kind. A
second "forgot password" click cannot be replayed after the first has already changed the password,
and a verification link left in an inbox stops working once the address is verified.

A verification link that expired, was lost, or never arrived is replaced through
`POST /api/auth/resend-verification` (UC-15). That one is authenticated and takes no body — the
person is read from the bearer token, so a caller can only ever ask for their own link — and it
retires the outstanding ones before mailing a new one, so only the newest link works. An address that
is already verified is refused: a link mailed to it could do nothing when clicked.

> A Mailgun failure is logged, never surfaced. `POST /api/auth/password-recovery` must answer
> identically whether or not the address belongs to anyone, so an outage cannot be allowed to turn
> into a 500 that tells an anonymous caller their guess was a real account.

### Google Sign-In

`POST /api/auth/google` (UC-25) signs a Google account up or in against a scope: the caller sends the
ID token they obtained from Google plus the scope's `PublicId`, and gets back the same kind of bearer
token a password login yields. The first call for a given Google account in a given scope creates the
Google User; every later one authenticates it.

Two things must be true before it works. The scope must have Google Sign-In switched on through
`PUT /api/scopes/{id}/google-signin` (UC-24) — it is off by default — and the API must know which
OAuth client the tokens were minted for:

| Variable | Value |
| --- | --- |
| `HEIMDALL_GOOGLE_CLIENT_IDS` | Comma-separated Google OAuth client IDs accepted as an ID token's audience |

Leave it unset and Google sign-in **refuses every token** with a 401, warning once at start-up. Unlike
the email fallback, this is not a convenience mode: verification needs an audience to check against
(NFR-13), and a verifier with no configured client could only reject everything or trust everything —
so it rejects. Every other endpoint keeps working; a scope that never enabled the feature is
unaffected either way.

Google sign-in only ever produces a `User`-equivalent identity (FR-GO-04). It cannot create or
authenticate a Scope Admin or System Admin, whatever the Google account is.

`POST /api/auth/google/sign-out` (UC-26) ends a Google session. It revokes nothing — tokens here are
stateless signed JWTs with an expiry, the strategy UC-11 established — so the endpoint confirms the
caller still holds a live Google session and answers the success that tells the client to drop the
token. That check is the substance: authentication reads no database per request, so a token outlives
the account once it is deleted, and one naming a deleted or unknown Google User is refused.

The Google Users of a scope are administered through four endpoints, all nested under the scope they
belong to (FR-GO-06):

| Endpoint | Use case | Who |
| --- | --- | --- |
| `GET /api/scopes/{scopeId}/google-users` | UC-27 | System Admin, or an owner of the scope |
| `GET /api/scopes/{scopeId}/google-users/{id}` | UC-27 | The above, **or the Google User themselves** |
| `DELETE /api/scopes/{scopeId}/google-users/{id}` | UC-28 | System Admin, or an owner of the scope |
| `DELETE /api/scopes/{scopeId}/google-users/{id}/hard` | UC-29 | System Admin only |

The by-id read is the one that admits a Google User, and it is why that endpoint carries no role
attribute: a Google User's token is `User`-role, so anything strong enough to keep other Users out
would lock out the actor the use case grants. The listing admits none of them.

Both deletions accept a scope's `PublicId` in the path and refuse a Google User reached through the
wrong one. The logical delete is idempotent — repeating it answers 200 with `alreadyDeleted: true`
and writes nothing — and is honoured everywhere it matters: a deleted account cannot sign in
(UC-25), cannot sign out, and is absent from reads unless `includeDeleted=true`. The hard delete has
no idempotent path; a second call is a 404. Neither cascades, because a Google User owns nothing.

## Build

```bash
dotnet build src/ArturRios.Heimdall.sln
```

## Test

Run the whole suite:

```bash
dotnet test src/ArturRios.Heimdall.sln
```

Run one kind at a time — unit tests are isolated; functional tests run end-to-end against a real
PostgreSQL database provisioned by Testcontainers:

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"
```

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"
```

Every run writes a `.trx` under each project's `TestResults/` (git-ignored), so a failure is always
recorded by name rather than only as a console count. To chase a test that fails only occasionally,
repeat the run and let the harness collect the names:

```bash
python scripts/flake_hunt.py --runs 25
```

See the [Testing Specification Document](docs/requirements/Testing%20Specification%20Document.md) for
the full testing standard.

## Migrations

The schema is managed with **EF Core migrations, applied explicitly** — the API never migrates on
startup, and refuses to start when migrations are pending. Use the interactive migration menu to
**list, create (generate), or apply** migrations:

```bash
python scripts/migrations.py
```

It asks which environment file to load (for the connection string), then offers the migration
actions. Creating a migration prompts for its name and adds it to
`src/Infrastructure/ArturRios.Heimdall.Data/Migrations`. Requires `dotnet tool restore` (above)
to have been run once.

## Run

```bash
dotnet run --project src/Presentation/ArturRios.Heimdall.WebApi
```

## Legal

This project is **proprietary**. All rights reserved. Use is governed by the terms in the
[LICENSE](LICENSE) file. Copyright &copy; 2026 Artur Rios.
