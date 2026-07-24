# Repository Patterns

The canonical technology choices are in the
[Technology Stack Document](../../../../docs/requirements/Technology%20Stack%20Document.md). This file
captures **how those pieces fit together in this codebase**, so a new use case matches the existing
style. When in doubt, copy the shape of the already-implemented reference use cases:

- **UC-01 (Create Scope)** — the reference for a **write** flow (command + validator + handler +
  output + controller `POST` + messages/status map).
- **UC-02 (View Scope)** — the reference for **read** flows (query handlers + controller `GET`).

## Layered structure & responsibilities

| Layer / project | Holds | Reference files |
| --- | --- | --- |
| **Domain** (`…Domain`) | Entities & enums. Entities are anemic (data + navigation) unless a behavior is genuinely domain logic. | `Entities/Scope.cs`, `Entities/ScopeOwner.cs`, `Enums/Roles.cs` |
| **Application / Command** (`…Command`) | Write use cases: `*Command` input (extends `BaseCommand`), `Input/Validation/*Validator` (FluentValidation), `*CommandHandler` (implements `ICommandHandlerAsync<TCommand,TOutput>`), `Output/*CommandOutput`. | `Handlers/CreateScopeCommandHandler.cs`, `Input/CreateScopeCommand.cs`, `Input/Validation/CreateScopeCommandValidator.cs`, `Output/CreateScopeCommandOutput.cs` |
| **Application / Query** (`…Query`) | Read use cases: `*Query` input, `*QueryHandler` (`IQueryHandlerAsync` / `IPaginatedQueryHandlerAsync`), `*Output`. | `Handlers/GetScopeByIdQueryHandler.cs`, `Handlers/ListScopesQueryHandler.cs` |
| **Application / Shared** (`…Shared`) | Canonical message strings and their HTTP status map. | `Messages/ScopeMessages.cs`, `Messages/ScopeMessageMap.cs` |
| **Infrastructure / Data** (`…Data`) | EF Core `AppDbContext`, one `*DbMap` per entity (`EntityMaps/`), migrations, seeding. | `EntityMaps/ScopeDbMap.cs`, `EntityMaps/ScopeOwnerDbMap.cs` |
| **Presentation / WebApi** (`…WebApi`) | Controllers exposing the endpoints; DI registration in `Startup.AddDependencies`. | `Controllers/ScopeController.cs`, `Startup.cs` |

## Key conventions to follow

**Handlers return `DataOutput<T>`, they do not throw.** Failures are added as errors on the output
(`output.AddError(...)` / `output.WithErrors(...)`) using a canonical message from the `*Messages`
class; success uses `output.WithData(...).WithMessage(...)`. Each alternative flow (`AF-xx`) maps to
a specific error message. Comment the handler steps with the UC/AF they implement (see
`CreateScopeCommandHandler`).

**Repositories, not `DbContext`, in handlers.** Depend on `IAsyncReadOnlyRepository<T>` for reads and
`IAsyncRepository<T>` for writes (from `ArturRios.Data.Relational.Core`), and use `.Query()` with EF
Core LINQ. This is also what makes handlers unit-testable with `FakeRepository<T>`.

**Public vs internal IDs.** Inputs/outputs and routes use `PublicId` (GUID); foreign keys and joins
use internal `Id` (bigint). Never expose or accept internal `Id`. (System Requirements §4.0.)

**Messages + status map.** Add user-facing strings to the entity's `*Messages` class and map each to
an HTTP status in the `*MessageMap` (e.g. `ScopeMessageMap.StatusCodes`), which `ResponseResolver`
uses to choose the response code.

**Controllers are thin.** A controller action binds input, dispatches through `CommandMediator` /
`QueryMediator`, and returns `ResponseResolver.Resolve(result, statusMap: …)`. Authorization is
declared with `[RoleRequirement((int)Roles.X)]` (or `[AllowAnonymous]` for public endpoints) — the
authorization alternative flows are enforced here and verified by functional tests.

**Wire up DI.** Register new validators, command handlers (`ICommandHandlerAsync<…>`), and query
handlers (`IQueryHandlerAsync<…>` / `IPaginatedQueryHandlerAsync<…>`) in `Startup.AddDependencies`,
following the existing registrations for the scope use cases.

**Entity mapping & schema.** New/changed persistence needs a `*DbMap` and an EF Core migration
generated via the migration menu (`python scripts/migrations.py`) — never edit the database by hand,
and never migrate on startup (Development Workflow / README).

**Testing.** Every handler and endpoint is covered per the
[Testing Specification Document](../../../../docs/requirements/Testing%20Specification%20Document.md):
unit tests for handlers/domain behavior (Given/When/Then, `[UnitFact]`, `FakeRepository`, Moq, Bogus)
and functional tests for endpoints (`[FunctionalFact]`, `WebApiTest<Program>`, Testcontainers
PostgreSQL), asserting both the response and the database state.
