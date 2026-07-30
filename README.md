# ArturRios.IdentityManager

A centralized **identity management API** built with ASP.NET Core (.NET 10). It provides person
management, application (non-human identity) management, authentication and authorization,
password recovery, email verification, and Google Sign-In for multiple client systems through
**scope-based multi-tenancy** — each client system operates within its own isolated scope.

## Overview

- **Multi-tenant by scope.** Every `User` belongs to exactly one scope; a `ScopeAdmin` owns one or
  more scopes; a `SystemAdmin` governs the whole system and belongs to no scope.
- **Persons & applications.** Manages human identities (persons) and non-human identities
  (applications owned by a person within a scope).
- **Authentication.** Password-based login (JWT), password recovery, email verification, and
  optional Google Sign-In per scope.
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
  Domain/ArturRios.IdentityManager.Domain/      Domain entities & enums
  Application/
    ArturRios.IdentityManager.Command/          Command (write) handlers — CQRS
    ArturRios.IdentityManager.Query/            Query (read) handlers — CQRS
    ArturRios.IdentityManager.Shared/           Shared messages/contracts
  Infrastructure/ArturRios.IdentityManager.Data/ EF Core DbContext, entity maps, migrations, seeding
  Presentation/ArturRios.IdentityManager.WebApi/ ASP.NET Core Web API (entry point)
  ArturRios.IdentityManager.sln
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
  the 29 use cases with flows and alternative flows.
- [Operations & Infrastructure Document](docs/requirements/Operations%20%26%20Infrastructure%20Document.md) —
  technical foundation and the health-check feature.
- [Technology Stack Document](docs/requirements/Technology%20Stack%20Document.md) — the technologies,
  libraries, and versions the project is built on.
- [Testing Specification Document](docs/requirements/Testing%20Specification%20Document.md) — how each
  use case is tested (unit + functional standards).
- [Development Workflow Document](docs/requirements/Development%20Workflow%20Document.md) — how a use
  case goes from backlog to merged (branch, issue status, testing gate, PR).

## Use case status

Delivery tracker for the 29 use cases in the
[Use Case Specification Document](docs/requirements/Use%20Case%20Specification%20Document.md), plus
the platform work that is not itself a use case. Each one ships on its own branch, issue, and pull
request — see the
[Development Workflow Document](docs/requirements/Development%20Workflow%20Document.md).

**Legend:** ✅ done and merged &nbsp;·&nbsp; 🚧 in progress &nbsp;·&nbsp; ⬜ not started

### Scope Management

| Use case | Status | Issue |
| --- | --- | --- |
| UC-01: Create Scope | ✅ | [#2](https://github.com/artur-rios/identity-manager-api/issues/2) |
| UC-02: View Scope | ✅ | [#3](https://github.com/artur-rios/identity-manager-api/issues/3) |
| UC-03: Update Scope | ✅ | [#4](https://github.com/artur-rios/identity-manager-api/issues/4) |
| UC-04: Logical Delete Scope | ✅ | [#5](https://github.com/artur-rios/identity-manager-api/issues/5) |
| UC-05: Hard Delete Scope | ✅ | [#6](https://github.com/artur-rios/identity-manager-api/issues/6) |
| UC-21: Add Scope Owner | ⬜ | [#22](https://github.com/artur-rios/identity-manager-api/issues/22) |
| UC-22: Remove Scope Owner | ⬜ | [#23](https://github.com/artur-rios/identity-manager-api/issues/23) |
| UC-23: Promote User to Scope Owner | ⬜ | [#24](https://github.com/artur-rios/identity-manager-api/issues/24) |

### Person Management

| Use case | Status | Issue |
| --- | --- | --- |
| UC-06: Create Person | ✅ | [#7](https://github.com/artur-rios/identity-manager-api/issues/7) |
| UC-07: View Person | ✅ | [#8](https://github.com/artur-rios/identity-manager-api/issues/8) |
| UC-08: Update Person | ✅ | [#9](https://github.com/artur-rios/identity-manager-api/issues/9) |
| UC-09: Logical Delete Person | ✅ | [#10](https://github.com/artur-rios/identity-manager-api/issues/10) |
| UC-10: Hard Delete Person | ✅ | [#11](https://github.com/artur-rios/identity-manager-api/issues/11) |

### Authentication & Security

| Use case | Status | Issue |
| --- | --- | --- |
| UC-11: Login (Authenticate) | ✅ | [#12](https://github.com/artur-rios/identity-manager-api/issues/12) |
| UC-12: Request Password Recovery | ⬜ | [#13](https://github.com/artur-rios/identity-manager-api/issues/13) |
| UC-13: Reset Password | ⬜ | [#14](https://github.com/artur-rios/identity-manager-api/issues/14) |
| UC-14: Verify Email | ⬜ | [#15](https://github.com/artur-rios/identity-manager-api/issues/15) |
| UC-15: Resend Verification Email | ⬜ | [#16](https://github.com/artur-rios/identity-manager-api/issues/16) |

### Application Management

| Use case | Status | Issue |
| --- | --- | --- |
| UC-16: Create Application | ⬜ | [#17](https://github.com/artur-rios/identity-manager-api/issues/17) |
| UC-17: View Application | ⬜ | [#18](https://github.com/artur-rios/identity-manager-api/issues/18) |
| UC-18: Update Application | ⬜ | [#19](https://github.com/artur-rios/identity-manager-api/issues/19) |
| UC-19: Logical Delete Application | ⬜ | [#20](https://github.com/artur-rios/identity-manager-api/issues/20) |
| UC-20: Hard Delete Application | ⬜ | [#21](https://github.com/artur-rios/identity-manager-api/issues/21) |

### Google Sign-In

| Use case | Status | Issue |
| --- | --- | --- |
| UC-24: Enable/Disable Google Sign-In | ⬜ | [#25](https://github.com/artur-rios/identity-manager-api/issues/25) |
| UC-25: Sign Up / Sign In via Google | ⬜ | [#26](https://github.com/artur-rios/identity-manager-api/issues/26) |
| UC-26: Sign Out via Google | ⬜ | [#27](https://github.com/artur-rios/identity-manager-api/issues/27) |
| UC-27: View Google User | ⬜ | [#28](https://github.com/artur-rios/identity-manager-api/issues/28) |
| UC-28: Logical Delete Google User | ⬜ | [#29](https://github.com/artur-rios/identity-manager-api/issues/29) |
| UC-29: Hard Delete Google User | ⬜ | [#30](https://github.com/artur-rios/identity-manager-api/issues/30) |

### Platform

Not use cases, tracked separately.

| Item | Status | Issue |
| --- | --- | --- |
| Project scaffolding & initial infrastructure | ✅ | [#31](https://github.com/artur-rios/identity-manager-api/issues/31) |
| Health check (liveness + detailed dependency check, UC-30) | ✅ | [#32](https://github.com/artur-rios/identity-manager-api/issues/32) |

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
cp src/Presentation/ArturRios.IdentityManager.WebApi/Environments/.env src/Presentation/ArturRios.IdentityManager.WebApi/Environments/.env.local
```

Then fill in `IDENTITY_MANAGER_DATA_CONNECTIONSTRING` (a PostgreSQL connection string),
`IDENTITY_MANAGER_DATA_DATABASETYPE` (`PostgreSql`), and the `IDENTITY_MANAGER_MASTER_USER_*`
values used to seed the first system administrator.

## Build

```bash
dotnet build src/ArturRios.IdentityManager.sln
```

## Test

Run the whole suite:

```bash
dotnet test src/ArturRios.IdentityManager.sln
```

Run one kind at a time — unit tests are isolated; functional tests run end-to-end against a real
PostgreSQL database provisioned by Testcontainers:

```bash
dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"
```

```bash
dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Functional"
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
`src/Infrastructure/ArturRios.IdentityManager.Data/Migrations`. Requires `dotnet tool restore` (above)
to have been run once.

## Run

```bash
dotnet run --project src/Presentation/ArturRios.IdentityManager.WebApi
```

## Legal

This project is **proprietary**. All rights reserved. Use is governed by the terms in the
[LICENSE](LICENSE) file. Copyright &copy; 2026 Artur Rios.
