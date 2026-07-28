# Technology Stack Document — Identity Manager API

## 1. Purpose

This document is the **single source of truth for the technologies used to build the Identity Manager API** — the runtime platform, language, first-party libraries, database, data-access stack, cross-cutting concerns, and testing tools, together with the version each is pinned to and the role it plays.

Every other document in this folder **references this document** for technical choices instead of restating them, so that:

- The domain documents ([Vision](Vision%20Document.md), [System Requirements](System%20Requirements%20Document.md), [Use Case Specification](Use%20Case%20Specification%20Document.md)) stay focused on *what* the system does.
- The [Operations & Infrastructure Document](Operations%20%26%20Infrastructure%20Document.md) stays focused on the platform's structure and operations.
- The [Testing Specification Document](Testing%20Specification%20Document.md) stays focused on *how* to test.
- Technology versions and roles are maintained in exactly **one** place.

> **Rule:** when a technology choice changes, it changes here first. Other documents link to this one rather than duplicating the detail.

---

## 2. Platform & Language

| Concern | Choice | Notes |
| --- | --- | --- |
| Runtime / framework | **.NET 10** (`net10.0`) | Every project targets `net10.0`. The Web API uses the `Microsoft.NET.Sdk.Web` SDK; libraries use `Microsoft.NET.Sdk`. |
| Language | **C# 14** | C# 14 is the default language version for `net10.0`, so it is used implicitly — no explicit `<LangVersion>` is set, which keeps the language pinned to the target framework's default. |
| Language features | `Nullable` **enabled**, `ImplicitUsings` **enabled** | Applied consistently across all production and test projects. |

**Why explicit:** C# 14 ships with and is the default for .NET 10. New code may freely use C# 14 features (collection expressions, primary constructors — already used e.g. in `Startup`, etc.). Do **not** pin an older `<LangVersion>`; let it track the .NET 10 default.

---

## 3. First-Party Libraries (`ArturRios.*`)

The project is built on a set of the author's own reusable libraries, all published under the **`ArturRios`** package prefix. They are consumed as NuGet `PackageReference`s. The table lists every one currently in use, its pinned version, where it is referenced, and its role.

| Package | Version | Referenced by | Role |
| --- | --- | --- | --- |
| **ArturRios.Util** | `1.4.2` | Domain, Shared, Data | Core cross-cutting utilities. Provides the standard `DataOutput<T>` result type (namespace `ArturRios.Output`) that handlers return, password **hashing** helpers (`ArturRios.Util.Hashing`), **HTTP** helpers (`ArturRios.Util.Http`), and general-purpose helpers used throughout the codebase. |
| **ArturRios.Util.WebApi** | `2.1.0` | WebApi | Web API foundation. Supplies the `WebApiStartup` base class, environment/configuration loading (`ArturRios.Util.WebApi.Configuration`), the security stack (role attributes/enums/extensions/middleware — e.g. `[AllowAnonymous]`, role requirements), **JWT** issuance & validation (namespace `ArturRios.Jwt`), exception & authentication middleware, Swagger-with-JWT wiring, and the `ResponseResolver` that maps a `DataOutput<T>` to an HTTP response. |
| **ArturRios.Mediator** | `1.0.3` | Command, Query, WebApi | Lightweight **CQRS mediator**. Provides `CommandMediator` / `QueryMediator` and the handler contracts (`ICommandHandlerAsync`, `IQueryHandlerAsync`, `IPaginatedQueryHandlerAsync`) that dispatch a command/query to its single handler. |
| **ArturRios.Data.Relational.Core** | `3.0.2` | Command, Query, Domain | Provider-agnostic **relational data layer**. Provides entity base types, the repository abstractions the handlers depend on (`IAsyncRepository<T>`, `IAsyncReadOnlyRepository<T>`), the EF Core `DbContext` base plus diagnostics options, and the DI entry point `AddDataConfigFromEnvironment<TDbContext>(prefix)` that binds the context to a connection from the environment. |
| **ArturRios.Data.PostgreSql** | `3.0.0` | Data | **PostgreSQL binding** for the relational core. Provides `AddPostgreSqlProvider()`, which wires EF Core to Npgsql so the relational layer runs against PostgreSQL. |
| **ArturRios.Util.Test** | `2.2.0` | all test projects | **Testing toolkit** (see §7). Provides the category test attributes (`[UnitFact]`/`[UnitTheory]`, `[FunctionalFact]`/`[FunctionalTheory]`), the `WebApiTest<TEntryPoint>` functional base class, `FakeRepository<T>`, `AsyncFakeRepository<T>` (async repository fake with an async-capable `Query()`), `FakeScheduler`, and `CustomAssert`. |

> `ArturRios.Output` and `ArturRios.Jwt` are **namespaces** surfaced by the packages above (the `ArturRios.Util` family and `ArturRios.Util.WebApi` respectively), not separately referenced packages.

---

## 4. Relational Database

| Concern | Choice |
| --- | --- |
| Relational database | **PostgreSQL** — the sole supported relational database for the project. |
| Provider integration | `ArturRios.Data.PostgreSql` → `AddPostgreSqlProvider()` (EF Core over Npgsql). |
| Connection configuration | Read from environment variables `IDENTITY_MANAGER_DATA_CONNECTIONSTRING` and `IDENTITY_MANAGER_DATA_DATABASETYPE` (`PostgreSql`) via `AddDataConfigFromEnvironment<AppDbContext>("IDENTITY_MANAGER_DATA")`. |

PostgreSQL is used in **every** environment, including automated tests — functional tests run against a real PostgreSQL instance provisioned by Testcontainers (never an in-memory provider), so behavior matches production. There is no secondary/alternate relational engine.

---

## 5. Data Layer (Entity Framework Core)

| Concern | Choice | Version |
| --- | --- | --- |
| ORM | **Entity Framework Core** (code-first) | 10.x |
| Design-time / migrations | `Microsoft.EntityFrameworkCore.Design` (enables `dotnet ef`; the `Data` library is its own startup project) | `10.0.10` |
| Naming convention | `EFCore.NamingConventions` — maps entities to **`snake_case`, singular** table/column names | `10.0.1` |
| Context | `AppDbContext`, based on the `ArturRios.Data.Relational.Core` context base, configured via entity maps (`ArturRios.IdentityManager.Data.EntityMaps`) | — |
| Diagnostics | `DbContextDiagnosticsOptions` — sensitive-data logging & detailed errors are **on only outside Production** (they would expose password hashes, salts, e-mails) | — |
| Startup seeding | `DatabaseSeeder` seeds the roles and the master System Admin on startup | — |

The data access pattern is **repository-based**: application handlers depend on `IAsyncReadOnlyRepository<T>` / `IAsyncRepository<T>` (from `ArturRios.Data.Relational.Core`) rather than on `DbContext` directly, which is also what makes them unit-testable with `FakeRepository<T>`.

---

## 6. Cross-Cutting Technologies

| Concern | Technology | Version | How it is used |
| --- | --- | --- | --- |
| Input validation | **FluentValidation** | `12.1.1` | Command inputs have `IValidator<TCommand>` implementations (e.g. `CreateScopeCommandValidator`), registered in DI and invoked inside the handlers. |
| Logging | **Serilog** (`Serilog`, `Serilog.AspNetCore`, `Serilog.Sinks.Map`) | `4.4.0` / `10.0.0` / `2.0.0` | Structured logging wired through `Host.UseSerilog()`, with JSON formatting; the log directory is configurable via environment variable. |
| Authentication / authorization | **JWT** via `ArturRios.Util.WebApi` (namespace `ArturRios.Jwt`) | (see §3) | Signed bearer tokens; issuer, audience, secret, and expiration are supplied via `IDENTITY_MANAGER_AUTH_*` environment variables. Role-based authorization and an `AuthenticationMiddleware` gate the endpoints. |
| Result / error model | `DataOutput<T>` (namespace `ArturRios.Output`, from the `ArturRios.Util` family) | (see §3) | Handlers return success/errors/messages/data on a `DataOutput<T>` instead of throwing; `ResponseResolver` maps it to an HTTP response. |
| API documentation | **Swagger / OpenAPI** (via `ArturRios.Util.WebApi`) | — | Enabled with JWT auth support (`UseSwaggerGen(jwtAuthentication: true)`). |
| Configuration | `.env.<environment>` files + environment variables | — | Loaded by the `ArturRios.Util.WebApi` configuration loader; `.env*` files are copied next to the built assembly. |

---

## 7. Testing Technologies

These are the technologies mandated for tests. **How** they are applied to each use case (naming, structure, coverage, the per-use-case workflow) is defined in the [Testing Specification Document](Testing%20Specification%20Document.md); this section is the canonical list of the tools and versions.

| Concern | Technology | Version | How it is used |
| --- | --- | --- | --- |
| Test framework | **xUnit** (`xunit`, `xunit.runner.visualstudio`) | `2.9.3` / `3.1.5` | The test framework for every test project. |
| Test SDK / runner | `Microsoft.NET.Test.Sdk` | `18.8.1` | Test host/runner integration for `dotnet test` and IDEs. |
| Coverage | `coverlet.collector` | `10.0.1` | Collects code coverage during test runs. |
| Test helpers & doubles | **`ArturRios.Util.Test`** | `2.2.0` | Category attributes (`[UnitFact]`/`[FunctionalFact]`, which stamp a `Category` trait), the `WebApiTest<TEntryPoint>` functional base class (spins up the host via `WebApplicationFactory<T>` and exposes an `HttpGateway` + authentication helpers), `FakeRepository<T>`, `AsyncFakeRepository<T>` (async repository fake whose `Query()` is async-capable, for unit-testing handlers that depend on `IAsyncReadOnlyRepository<T>`/`IAsyncRepository<T>`), `FakeScheduler`, and `CustomAssert`. |
| Mocking | **Moq** | `4.20.72` | The single mocking library for stubbing non-repository collaborators (validators, mediators, services). Do not introduce a second mocking framework. |
| Test data generation | **Bogus** | `35.6.3` | The standard way to generate entities/commands/DTOs (`Faker<T>`) instead of large inline literals or shared fixtures. |
| Functional database | **Testcontainers** (`Testcontainers.PostgreSql`) | `4.13.0` | Provisions a real, throwaway **PostgreSQL** container for functional (end-to-end) tests, so tests run against the same engine as production. |

> **Note:** Moq and Bogus were pinned to the versions above when the first tests needing them were written (UC-03's handler unit tests). Keep every test project on the same versions.

Tests are split by **category** — unit tests exercise Command/Query handlers and Domain behavior in isolation; functional tests exercise the Web API end-to-end against Testcontainers PostgreSQL. Run one kind at a time with `dotnet test --filter "Category=Unit"` or `"Category=Functional"`.

---

## 8. Version Summary

| Category | Package / Tool | Version |
| --- | --- | --- |
| Platform | .NET | `10` (`net10.0`) |
| Language | C# | `14` (framework default) |
| First-party | ArturRios.Util | `1.4.2` |
| First-party | ArturRios.Util.WebApi | `2.1.0` |
| First-party | ArturRios.Util.Test | `2.2.0` |
| First-party | ArturRios.Mediator | `1.0.3` |
| First-party | ArturRios.Data.Relational.Core | `3.0.2` |
| First-party | ArturRios.Data.PostgreSql | `3.0.0` |
| Data | Microsoft.EntityFrameworkCore.Design | `10.0.10` |
| Data | EFCore.NamingConventions | `10.0.1` |
| Validation | FluentValidation | `12.1.1` |
| Logging | Serilog | `4.4.0` |
| Logging | Serilog.AspNetCore | `10.0.0` |
| Logging | Serilog.Sinks.Map | `2.0.0` |
| Testing | xunit | `2.9.3` |
| Testing | xunit.runner.visualstudio | `3.1.5` |
| Testing | Microsoft.NET.Test.Sdk | `18.8.1` |
| Testing | coverlet.collector | `10.0.1` |
| Testing | Testcontainers.PostgreSql | `4.13.0` |
| Testing | Moq | `4.20.72` |
| Testing | Bogus | `35.6.3` |
