# UC-06: Create Person — Design

## Summary

Implement UC-06 (Create Person, FR-PE-01/02/09/10/11, FR-RO-01/02/03, FR-SC-12, FR-EV-01/02):
register a new person through **three distinct paths**, each its own endpoint, actor set, and
scope-association rule:

| Path | Endpoint | Creates | Scope association | Actors |
| --- | --- | --- | --- | --- |
| a | `POST /api/scopes/{scopeId}/persons` | `User` | `SCOPE_USER` row | System Admin, Scope Admin (owner) |
| b | `POST /api/persons` | `ScopeAdmin` or `SystemAdmin` | none | System Admin only |
| c | `POST /api/scopes/{scopeId}/owners` | `ScopeAdmin` | `SCOPE_OWNER` row (co-owner) | System Admin, Scope Admin (owner) |

Every path hashes the password with a per-person random salt, creates the person with
`IsDeleted = false` / `EmailVerified = false`, issues and persists a time-limited
`EmailVerificationToken`, dispatches a verification email through a **stubbed sender**, and returns the
created person (never `PasswordHash` / `Salt`).

All three paths ship together on `feature/uc-06-create-person`, in one PR (matching the workflow's
one-UC-one-PR convention).

**No schema change / no EF migration:** `person`, `scope_user`, `scope_owner`, and
`email_verification_token` tables and their `*DbMap`s already exist from `InitialCreate`.

## Decisions (from brainstorming)

1. **All three paths in one PR.**
2. **Email verification depth — token + stubbed sender.** Issue and persist an
   `EmailVerificationToken`; dispatch through a new `IEmailVerificationSender` implemented now as a
   logging/no-op stub. Real SMTP delivery is deferred (its own infrastructure concern; UC-14/UC-15
   will consume the same token mechanism).
3. **AF-06e enforced now via DB ownership lookup.** The controller reads the `AuthenticatedUser` the
   auth middleware attaches to the request and copies its `id` / `role` onto the path-a / path-c
   commands; the handler verifies scope ownership against `SCOPE_OWNER`. `[RoleRequirement]` handles
   the role-level gate (AF-06c) declaratively.

## Routing — new `PersonController`

A new `PersonController` owns all three person-creating endpoints, using explicit per-action route
templates so scope-nested creation stays out of `ScopeController` (which remains focused on scope
operations):

- `[HttpPost("api/scopes/{scopeId:guid}/persons")]` — path a — `[RoleRequirement(SystemAdmin, ScopeAdmin)]`
- `[HttpPost("api/persons")]` — path b — `[RoleRequirement(SystemAdmin)]`
- `[HttpPost("api/scopes/{scopeId:guid}/owners")]` — path c — `[RoleRequirement(SystemAdmin, ScopeAdmin)]`

Each action is thin: bind input, copy the route `scopeId` and (for a / c) the acting user onto the
command, dispatch through `CommandMediator`, and return
`ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes)`.

*(Path c's route lives under `/scopes/{id}/owners`; UC-21/22/23 will later add sibling
owner-management routes. Those belong to `ScopeController`/owner management — only the person-creating
`POST …/owners` is in scope here.)*

## Actor identity (AF-06e plumbing)

- The JWT / `AuthenticatedUser` (ArturRios.Util.WebApi.Security) carries only `id` (int) and `role`
  (int) — no scope claims. FR-AU-04's owned-scope claims are UC-11 (Login) territory, not yet built.
- By the existing de-facto convention, the `id` claim is the person's internal `Id` (the seeded master
  admin is id 1; `TestTokens.ForRole` mints `AuthenticatedUser(1, role)`). UC-06 treats the `id`
  claim as `Person.Id` for the ownership lookup.
- `BaseCommand` carries no user, so the controller reads the authenticated user the
  `AuthenticationMiddleware` attaches to the request and sets `ActingPersonId` / `ActingRole` on the
  path-a / path-c commands. (Exact accessor — `HttpContext.Items` key vs. `HttpContext.User` claims —
  is confirmed during implementation via a functional test; the design depends only on "the acting
  user's id + role reach the handler".)
- Path b needs no acting identity: it is System-Admin-only and creates no scope association.

## Role representation (path b)

`CreateAdminCommand.Role` is the `Roles` enum value (`SystemAdmin = 1`, `ScopeAdmin = 2`), validated to
be one of those two. This matches how roles are represented everywhere external-facing already — token
`role` claims and `[RoleRequirement((int)Roles.X)]` — and avoids exposing the `Role` table's
generated `PublicId`, which is closed reference data clients do not know. The seeder guarantees
`Role.Id == (long)Roles`, so `RoleId` is assigned directly from the enum value.

## Commands, validators, output

Three commands (`: BaseCommand`), three validators (shape-only, AF-06d), one shared output.

| Type | Fields |
| --- | --- |
| `CreateUserCommand` (a) | `ScopeId` (Guid, route), `Name`, `Email`, `Password`; `ActingPersonId` (long), `ActingRole` (int) |
| `CreateAdminCommand` (b) | `Name`, `Email`, `Password`, `Role` (int enum ∈ {SystemAdmin, ScopeAdmin}) |
| `CreateScopeOwnerCommand` (c) | `ScopeId` (Guid, route), `Name`, `Email`, `Password`; `ActingPersonId` (long), `ActingRole` (int) |
| `CreatePersonCommandOutput` (`: CommandOutput`) | `Id` (PublicId), `Name`, `Email`, `Role` (int), `EmailVerified`, `ScopeId?` (PublicId; set for a / c), `CreatedAt` |

`ActingPersonId` / `ActingRole` are set by the controller, never bound from the request body.

**Validators** (`CreateUserCommandValidator`, `CreateAdminCommandValidator`,
`CreateScopeOwnerCommandValidator`) check only request shape; business rules that need data access
(uniqueness, scope existence, ownership) live in the handlers:

- `Name` — not empty, max 200.
- `Email` — not empty, valid email format.
- `Password` — not empty, minimum length (8).
- `Role` (path b only) — must equal `SystemAdmin` or `ScopeAdmin`.

## Handlers

Each handler returns `DataOutput<CreatePersonCommandOutput?>`, never throws, and reports failures as
errors carrying a canonical `PersonMessages` value (so `ResponseResolver` picks the status from
`PersonMessageMap`). Handler steps are commented with the UC/AF they implement, matching
`CreateScopeCommandHandler`.

**Shared per-path skeleton:** validate → (scope + ownership checks) → email-uniqueness check → hash
password (`Hash.EncodeWithRandomSalt`) → build `Person` (+ join row) → persist → issue + send
verification token (shared service) → return output.

### `CreateUserCommandHandler` (path a)
Deps: `IValidator<CreateUserCommand>`, read repos for `Scope`, `Person`; write repo for `Person`;
`IEmailVerificationService`.
1. Validate (AF-06d).
2. Load scope by `PublicId`, not logically deleted → else **AF-06b** (`ScopeNotFound`, 404).
3. **AF-06e:** if `ActingRole == ScopeAdmin`, require a `SCOPE_OWNER(scope.Id, ActingPersonId)` row →
   else `NotScopeOwner` (403). `SystemAdmin` bypasses.
4. **AF-06a:** email must be unique among that scope's `User` persons
   (`p.ScopeMembership.ScopeId == scope.Id && p.Email == email && !p.IsDeleted`) → else
   `EmailAlreadyExists` (409).
5. Create `Person { RoleId = User, ... }` with a `ScopeUser { ScopeId = scope.Id }` and persist.
6. Issue + send verification token; return `Id`, `ScopeId = scope.PublicId`, `PersonCreatedSuccessfully`.

### `CreateAdminCommandHandler` (path b)
Deps: `IValidator<CreateAdminCommand>`, read + write repos for `Person`; `IEmailVerificationService`.
1. Validate (AF-06d, incl. role ∈ {SystemAdmin, ScopeAdmin}).
2. **AF-06a:** email must be unique among **admin** persons system-wide
   (`(p.RoleId == SystemAdmin || p.RoleId == ScopeAdmin) && p.Email == email && !p.IsDeleted`) → else
   `EmailAlreadyExists` (409).
3. Create `Person { RoleId = command.Role, ... }`, no join row, and persist.
4. Issue + send verification token; return `Id` (no `ScopeId`), `PersonCreatedSuccessfully`.
(AF-06c — non-System-Admin — is enforced by `[RoleRequirement]`; functional test only.)

### `CreateScopeOwnerCommandHandler` (path c)
Deps: `IValidator<CreateScopeOwnerCommand>`, read repos for `Scope`, `Person`; write repo for
`Person`; `IEmailVerificationService`.
1. Validate (AF-06d).
2. Load scope by `PublicId`, not logically deleted → else **AF-06b** (404).
3. **AF-06e:** same ownership check as path a.
4. **AF-06a:** email unique among admin persons system-wide (as path b) → else 409.
5. Create `Person { RoleId = ScopeAdmin, ... }` with a `ScopeOwner { ScopeId = scope.Id }` and persist.
6. Issue + send verification token; return `Id`, `ScopeId = scope.PublicId`, `PersonCreatedSuccessfully`.

Persons are created through the writable `Person` repository with their join row attached on the
navigation (`ScopeMembership` / `ScopeOwnerships`), the same insert-graph style
`CreateScopeCommandHandler` uses for `Scope.Owners`.

## Email verification (FR-EV-01 / FR-EV-02)

Extracted into a shared collaborator so the three handlers do not triplicate token logic:

- **`IEmailVerificationService`** (Command/Application layer) — `IssueAndSendAsync(Person person)`:
  builds `EmailVerificationToken { PersonId = person.Id, Token = <random>, ExpiresAt = now + TTL,
  Used = false }`, persists it via `IAsyncRepository<EmailVerificationToken>`, then calls the sender.
  - Token string via `ArturRios.Util.Random.CustomRandom.Text` (URL-safe alphanumeric,
    sufficient length).
  - TTL from an env var (`IDENTITY_MANAGER_EMAIL_VERIFICATION_TOKEN_EXPIRATION_IN_SECONDS`) with a
    default (24h), mirroring the JWT-config env pattern in `Startup`.
- **`IEmailVerificationSender`** — `SendAsync(string email, string token)`; implemented now as
  `LoggingEmailVerificationSender`, which logs recipient + token (no real delivery).

Handlers depend on `IEmailVerificationService` (mocked in handler unit tests). The service is unit
-tested on its own against `AsyncFakeRepository<EmailVerificationToken>` and a mocked sender.

Failure to *send* the email does not roll back a created person (the token is already persisted for
later resend, UC-15); the stub cannot fail, so this is a note for the real implementation, not
behavior under test here.

## Messages & status map

New `PersonMessages` + `PersonMessageMap` (mirroring `ScopeMessages` / `ScopeMessageMap`):

| Message | Status | Flow |
| --- | --- | --- |
| `PersonCreatedSuccessfully` | 201 Created | main flow |
| `EmailAlreadyExists` | 409 Conflict | AF-06a |
| `ScopeNotFound` | 404 Not Found | AF-06b |
| `NotScopeOwner` | 403 Forbidden | AF-06e |
| `NameRequired`, `NameTooLong`, `EmailRequired`, `EmailInvalid`, `PasswordRequired`, `PasswordTooShort`, `InvalidRole` | 400 Bad Request | AF-06d |

## Dependency injection (`Startup.AddDependencies`)

Register: the three validators (`IValidator<Create*Command>`), the three handlers
(`ICommandHandlerAsync<Create*Command, CreatePersonCommandOutput>`), `IEmailVerificationService` →
its implementation, and `IEmailVerificationSender` → `LoggingEmailVerificationSender`. Following the
existing scope registrations.

## Components

| Layer | File | New/Edit |
| --- | --- | --- |
| Command / Input | `Command/Input/CreateUserCommand.cs`, `CreateAdminCommand.cs`, `CreateScopeOwnerCommand.cs` | new |
| Command / Validation | `Command/Input/Validation/CreateUserCommandValidator.cs`, `CreateAdminCommandValidator.cs`, `CreateScopeOwnerCommandValidator.cs` | new |
| Command / Handlers | `Command/Handlers/CreateUserCommandHandler.cs`, `CreateAdminCommandHandler.cs`, `CreateScopeOwnerCommandHandler.cs` | new |
| Command / Output | `Command/Output/CreatePersonCommandOutput.cs` | new |
| Command / Services | `Command/Services/IEmailVerificationService.cs` + impl; `IEmailVerificationSender.cs` + `LoggingEmailVerificationSender.cs` | new |
| Shared / Messages | `Shared/Messages/PersonMessages.cs`, `Shared/Messages/PersonMessageMap.cs` | new |
| Presentation | `WebApi/Controllers/PersonController.cs` | new |
| DI | `WebApi/Startup.cs` | edit |

(Exact folder for the email-verification service within the Command project is finalized in the plan;
it stays in the Application layer so handlers depend on an interface, not infrastructure.)

## Testing (Testing Spec §6–§7)

Unit tests use `AsyncFakeRepository<T>` (one instance = reader + writer), **Bogus** for entities, and
**Moq** for the validator and `IEmailVerificationService`. GWT naming, `// Given / // When / // Then`.

**Unit — `Command.Tests`:**
- `CreateUserCommandHandlerTests`: main flow (User + `SCOPE_USER` created, token issued); AF-06b
  (scope missing / deleted); AF-06e (ScopeAdmin not owner → error; owner → success; SystemAdmin
  bypasses); AF-06a (duplicate email in scope). AF-06d via `CreateUserCommandValidatorTests`.
- `CreateAdminCommandHandlerTests`: main flow (each of ScopeAdmin / SystemAdmin, no join row);
  AF-06a (duplicate admin email system-wide). AF-06d + invalid-role via `CreateAdminCommandValidatorTests`.
- `CreateScopeOwnerCommandHandlerTests`: main flow (ScopeAdmin + `SCOPE_OWNER` created); AF-06b;
  AF-06e; AF-06a. AF-06d via `CreateScopeOwnerCommandValidatorTests`.
- `EmailVerificationServiceTests`: token built, persisted (right person, future expiry, `Used=false`),
  sender invoked with the person's email + token.

**Functional — `WebApi.Tests` (Testcontainers PostgreSQL, assert response + DB state):**
- Path a: owner ScopeAdmin and SystemAdmin each create a User → 201; `person` row (`RoleId=User`,
  `EmailVerified=false`, hash+salt set), `scope_user` row, `email_verification_token` row present.
  AF-06a → 409; AF-06b → 404; AF-06e (ScopeAdmin not owner) → 403; auth (User role) → 403;
  unauthenticated → 401.
- Path b: SystemAdmin creates ScopeAdmin and SystemAdmin → 201; person row, **no** join row.
  AF-06a → 409; AF-06c (ScopeAdmin / User token) → 403; invalid role / bad input → 400.
- Path c: owner ScopeAdmin and SystemAdmin each create a co-owner → 201; person (`RoleId=ScopeAdmin`)
  + `scope_owner` row. AF-06a → 409; AF-06b → 404; AF-06e → 403; auth → 403 / 401.

## Out of scope / non-goals

- No real email delivery (stub sender only); no email templating/SMTP configuration.
- No login/token issuance (UC-11); UC-06 relies on `TestTokens` for functional auth, as prior UCs do.
- No person read/update/delete (UC-07 – UC-10); no owner-management routes beyond path c's create
  (UC-21/22/23).
- No schema change / no migration.
