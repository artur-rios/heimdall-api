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
