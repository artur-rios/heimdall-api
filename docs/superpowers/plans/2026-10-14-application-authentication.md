# Application Authentication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a registered application authenticate to Heimdall as itself (#178). Admins issue it up to two revocable secrets (UC-53 to UC-55). The application exchanges a secret for a 10-minute token under its own audience (UC-56). Consumers such as Cerberus re-check that token live through `GET /api/applications/self` (UC-57).

**Architecture:** The existing CQRS shape. Credential management is a command, a query and a command behind a new `ApplicationCredentialController`, all sharing the access rule `GET`/`PUT /api/scopes/{scopeId}/applications/{id}` already apply. Token exchange is an anonymous, rate-limited, audited command. The live re-check is an anonymous query whose handler validates the bearer token itself. Both live behind a new `ApplicationTokenController`. One class in the Web API signs and validates application tokens with the person tokens' key pair, under a separate, required audience. Heimdall's own middleware never accepts one, because the claims mapper refuses any token carrying `tokenKind`. A new `application_credential` table stores only SHA-256 hashes of the secrets. A row lock on the application enforces the two-live-credentials limit.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core 10 on PostgreSQL 18 (Npgsql, EFCore.NamingConventions), FluentValidation, Microsoft.IdentityModel.JsonWebTokens, xUnit v3 (`xunit.v3.mtp-off`) + Moq, Testcontainers, `ArturRios.*` packages (`Jwt`, `Mediator`, `Output`, `Data.Relational.Core`, `Util.Test`, `Util.WebApi`).

**Spec:** `docs/superpowers/specs/2026-10-14-application-authentication-design.md` (amended in commit 01d2c9e: read §2 and §3 as amended).

## Decisions made in this plan

The spec leaves these open; the plan fixes them as follows.

1. **Names.**
   - Entity `ApplicationCredential`, table `application_credential`, DbSet `ApplicationCredentials`, navigation `Application.Credentials`.
   - Commands and query, with their outputs:
     - `IssueApplicationCredentialCommand` → `IssueApplicationCredentialCommandOutput`
     - `ListApplicationCredentialsQuery` → `ApplicationCredentialListOutput`, holding `ApplicationCredentialOutput` items
     - `RevokeApplicationCredentialCommand` → `RevokeApplicationCredentialCommandOutput`
     - `IssueApplicationTokenCommand` → `IssueApplicationTokenCommandOutput`
     - `CheckApplicationTokenQuery` → `ApplicationTokenCheckOutput`
   - The command type names are what the audit trail records as `Action`.
2. **Routes.** `ApplicationCredentialController` is at `api/scopes/{scopeId:guid}/applications/{id:guid}/credentials`:
   - `POST` → `Issue`
   - `GET` → `List`
   - `DELETE {credentialId:guid}` → `Revoke`

   `ApplicationTokenController` is at `api/applications`:
   - `POST token` → `IssueToken`
   - `GET self` → `Self`
3. **Message classes and texts.** `ApplicationCredentialMessages` and `ApplicationCredentialMessageMap` serve the credential controller:

   | Message | Text | Status |
   | --- | --- | --- |
   | `CredentialIssued` | `"Application credential issued. Store the secret now: it is never shown again."` | 201 |
   | `CredentialsRetrieved` | `"Application credentials retrieved."` | 200 |
   | `CredentialRevoked` | `"Application credential revoked."` | 200 |
   | `ExpiryNotInFuture` | `"The expiry must be in the future."` | 400 |
   | `NotAuthorizedToManageCredentials` | `"You are not allowed to manage this application's credentials."` | 403 |
   | `CredentialNotFound` | `"Application credential not found."` | 404 |
   | `CredentialLimitReached` | `"This application already has two live credentials. Revoke one before issuing another."` | 409 |
   | `CredentialAlreadyRevoked` | `"This application credential is already revoked."` | 409 |

   A missing scope, a missing application, a logically deleted one, or one in another scope all reuse `ApplicationMessages.ApplicationNotFound` (404). `ApplicationTokenMessages` and `ApplicationTokenMessageMap` serve the token controller:

   | Message | Text | Status |
   | --- | --- | --- |
   | `TokenIssued` | `"Application token issued."` | 200 |
   | `TokenAccepted` | `"Application token accepted."` | 200 |
   | `ApplicationIdRequired` | `"Application id is required."` | 400 |
   | `ScopeIdRequired` | `"Scope id is required."` | 400 |
   | `SecretRequired` | `"Secret is required."` | 400 |
   | `SecretTooLong` | `"Secret must be at most 100 characters."` | 400 |
   | `CredentialsNotAccepted` | `"The application credentials were not accepted."` | 401 |
   | `TokenNotAccepted` | `"The application token was not accepted."` | 401 |
4. **Who may manage credentials.** The rule is `ApplicationManagementAccess.ResolveAsync`, in `Shared/Services`, because Query and Command both use it.
   - A System Admin may manage any application.
   - A Scope Admin may manage one only if they are its owner **and** still own its scope (UC-17 AF-17b).
   - The application must sit in the addressed scope, be live, and have a live scope; otherwise the answer is `NotFound`. Not-found is checked before authorization, as UC-17 does it.
5. **Secrets.**
   - `ApplicationCredentialSecret.New()` returns `hds_` plus 32 bytes from `RandomNumberGenerator`, base64url without padding: 47 characters.
   - `ApplicationCredentialSecret.HashOf(secret)` is `SingleUseTokenHash.Of(secret)`: lowercase SHA-256 hex, 64 characters.
   - The token endpoint trims the submitted secret before hashing, because a secret read from a file often carries a trailing newline.
6. **Liveness.** `ApplicationCredential.LiveAt(now)` is the one definition, written as an expression so EF can translate it: not revoked, and either no expiry or one after `now`. `IsLiveAt(now)` compiles that expression for in-memory use. `ApplicationCredential.MaxLive = 2`.
7. **The two-live limit under concurrency.** `IApplicationCredentialSlots.TryAddWithinLimitAsync(credential, maxLive, now)`, in `Domain/Persistence`, is implemented in `Data/Persistence/ApplicationCredentialSlots`. Inside one transaction it:
   1. locks the application's row with `SELECT 1 FROM <application table> WHERE id = @id FOR UPDATE`;
   2. counts the live credentials;
   3. inserts the new one only while the count is below the limit.

   Unit tests use `InMemoryApplicationCredentialSlots`.
8. **Token claims.** `ApplicationTokenClaims` in `Shared/Security` defines:

   | Constant | Claim |
   | --- | --- |
   | `TokenKind` | `"tokenKind"` |
   | `ApplicationKind` | `"application"` (the claim's value) |
   | `Id` | `"id"` |
   | `ScopeId` | `"scopeId"` |
   | `CredentialId` | `"credentialId"` |
   | `Name` | `"name"` |

   `IdentityUserMapper.FromClaims` returns `null` for any claim set that contains `tokenKind`.
9. **Signing and validating.** `JwtApplicationTokenService` (Web API, `Security/`) implements `IApplicationTokenIssuer` (`Command/Services`) and `IApplicationTokenValidator` (`Shared/Security`). It signs through `JwtHandler.CreateToken` with the person configuration and these overrides:
   - `Audience = options.Audience`
   - `ExpirationInSeconds = options.Lifetime.TotalSeconds`
   - `Claims = <the claims above>`

   It validates with `JsonWebTokenHandler` and checks:
   - the signature, against every configured key (current and previous), each as ASCII bytes the way `ArturRios.Jwt` does it;
   - HS256 only;
   - the issuer, **when one is configured**: `HEIMDALL_AUTH_TOKEN_ISSUER` defaults to empty, and an empty `ValidIssuer` would refuse every token;
   - the application audience;
   - the lifetime, with zero clock skew;
   - `tokenKind == application`.
10. **Configuration.** `ApplicationTokenOptions(string Audience, TimeSpan Lifetime)` in `WebApi/Security`, built by `FromEnvironment()` over a testable `Parse(audience, expirationInSeconds, personAudience)`:
    - Startup refuses, with an `InvalidOperationException` naming the variable, when `HEIMDALL_APPLICATION_TOKEN_AUDIENCE` is missing or blank, or when it equals `HEIMDALL_AUTH_TOKEN_AUDIENCE` (ordinal, after trimming).
    - `HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS` defaults to `600`. A value that won't parse, is not positive, or is above `86400` falls back to `600`.
    - `docker-compose.yml` defaults the audience to `heimdall-applications`, so a Compose deployment that upgrades keeps starting.
11. **Outputs.**
    - `IssueApplicationTokenCommandOutput` carries `Id` (the application's `PublicId`) beside `Token` and `ExpiresAt`. `AuditingCommandHandler` takes the audit target from `Id`, and the spec asks for the application as target. The caller already sent that id.
    - `IssueApplicationCredentialCommandOutput.Id` and `RevokeApplicationCredentialCommandOutput.Id` are the credential's `PublicId`.
12. **Offset-less instants.** `UtcInstant` moves from `Query/Input` to `Shared/Time` (namespace `ArturRios.Heimdall.Shared.Time`), and its tests move to `Shared.Tests`. UC-53's validator and handler read `expiresAt` through it.
13. **Retention.** `PurgeExpiredTokensCommandHandler` also removes credentials revoked or expired at or before the single-use-token grace cutoff. Its output gains `ApplicationCredentialsPurged`.
14. **Bearer reading on `GET /api/applications/self`.** The controller takes the `Authorization` header and strips a `Bearer ` prefix, matched case-insensitively. Anything else, or no header, becomes a `null` token, which the handler answers `401 TokenNotAccepted`.
15. **Documentation.**
    - Use Case Specification Document: UC-53 to UC-57, after UC-48 (UC-49 to UC-52 arrive with the open #182 and #183; whichever merges later keeps both sides).
    - System Requirements Document: FR-AP-10 to FR-AP-16 and §5.3 endpoint rows.
    - Threat Model: TH-33 to TH-35.
    - Data Protection Document and Data Retention Schedule.
    - A consumer guide at `docs/content/en/docs/flows/application-authentication.md`.
    - `api-client/http/applications.http` and five Bruno requests in `api-client/bruno/Applications/` (`seq` 7 to 11).
    - New environment variables `credentialId`, `applicationSecret` and `applicationToken`.
16. **OpenAPI** is regenerated in each task that changes an endpoint (Tasks 3, 4, 5 and 6).

## Global Constraints

- Work only in the worktree you were given (branch `feature/uc-53-application-credentials`). Never touch `/root/repositories/heimdall-api` itself or any other worktree. Do not push, do not use `gh`, and do not dispatch subagents.
- Read `CONTRIBUTING.md` and `docs/requirements/Development Workflow Document.md` before the first task. The Definition of Done applies:
  - unit tests for every handler's main flow and every alternative flow;
  - functional tests for every endpoint's main flow and every alternative flow, authorization included (401 anonymous, 403 wrong role or non-owning Scope Admin);
  - the Step 4.1 personal-data check for the migration.
- TDD: write the failing test, run it, and see it fail before writing the code.
  - Test names follow `GivenCondition_WhenAction_ThenOutcome`.
  - Each test body carries `// Given`, `// When` and `// Then`.
  - Every test has `[UnitFact]`/`[UnitTheory]` or `[FunctionalFact]`; CI fails a test with neither.
- Build with `dotnet build src/ArturRios.Heimdall.sln`, clean before every commit.
- Run test projects **one at a time** (Argon2id OOM-kills parallel runs): `dotnet test <project directory> --no-build`.
  - Clear an orphaned runner with `pkill -f 'testhost[.]dll'`, never `pkill -f testhost`.
  - `tests/Domain/ArturRios.Heimdall.Domain.Tests` reporting "No test is available" before Task 1 is expected.
- Functional-test traps:
  - Never assert on the gap between two instants stamped in one request.
  - Normalise `DataOutput.Timestamp` before comparing whole responses.
  - Each functional test gets a fresh host, so the `AuthAnonymous` limit is 10 requests a minute *per test*. Keep each test at or below 9 calls to `POST /api/applications/token`.
- Every write goes through `AddAuditedCommandHandler`, and its output carries the audit target's `PublicId` as `Id`. Reads are not audited.
- Every message a handler returns has an entry in its `*MessageMap`, with a comment citing the UC/AF.
- Internal `bigint` ids never leave the data layer (NFR-15); every identifier on an output is a `PublicId`. Route-bound and actor fields are `[JsonIgnore]` and set by the controller (route values, `HttpContext.ApplyActor`).
- A secret appears in exactly one response, UC-53's. It is never logged, never stored (only `SecretHash`), never in an audit entry, and never in an output of UC-54, UC-55, UC-56 or UC-57. No log template may contain `{Secret}` or `{Token}`.
- Every refusal of `POST /api/applications/token` is the same `401` with `CredentialsNotAccepted`. Every refusal of `GET /api/applications/self` is the same `401` with `TokenNotAccepted`. Neither may say which check failed. A `400` is only for a request missing a field or carrying an over-long secret.
- New migration: run `dotnet tool restore` once, then `dotnet ef` with `HEIMDALL_DATA_CONNECTIONSTRING="Host=localhost;Port=5432;Database=heimdall;Username=postgres;Password=postgres"` (a dummy; nothing connects). Replace the generated class's `/// <inheritdoc />` with a `<summary>`/`<remarks>` doc comment.
- XML doc comments on every new public type and member, citing the UC and FR numbers, at the density of the surrounding code.
- After changing an endpoint, run `python3 scripts/openapi.py`; `python3 scripts/openapi.py --check` must then pass. Never hand-edit `docs/openapi/heimdall.json`.
- Commits: a lowercase Conventional Commit subject of at most 50 characters, imperative, with the body wrapped at 72. Stage paths explicitly; never `git add -A` or `git add .`. End every commit message with exactly these two lines:
  ```
  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01KU2awQWyVHDRk4zc4mKYjv
  ```
- `python3 scripts/vulnerabilities.py` must exit 0. Do not bump packages.

## Review Focus

These are the five inputs the spec implies but its tests don't exercise that are most likely to bite someone using this, most likely first. Each line's test is in the named task.

1. **Two admins issuing at the same moment against an application with no credentials:** exactly two may become live, and the rest must answer `409 CredentialLimitReached`, never leaving three live secrets. Test: `GivenNoCredentials_WhenIssuingInParallel_ThenExactlyTwoAreLive` (Task 3).
2. **`expiresAt` sent without an offset** (`"2030-01-01T00:00:00"`, what a `datetime-local` input produces) must be read as UTC and answer `201`, not `500` from Npgsql refusing an unspecified `DateTime`. Tests: `GivenAnExpiryWithoutAnOffset_WhenValidating_ThenItIsReadAsUtc` (Task 3, unit) and `GivenAnExpiryWithoutAnOffset_WhenIssuing_ThenCreatedAndStoredAsUtc` (Task 3, functional).
3. **A deployment that leaves `HEIMDALL_AUTH_TOKEN_ISSUER` empty**, its documented default, must still validate the application tokens it issues, rather than refusing every one on an empty `ValidIssuer`. Test: `GivenNoIssuerConfigured_WhenRoundTripping_ThenTheTokenIsAccepted` (Task 2).
4. **A secret read from a file with a trailing newline** (`"hds_…\n"`) must be accepted as the same secret. Test: `GivenASecretWithSurroundingWhitespace_WhenExchanging_ThenATokenIsIssued` (Task 5).
5. **A credential that expires while a token issued from it is still within its ten minutes:** the live re-check must refuse that token, just as for revocation. Test: `GivenItsCredentialExpiredAfterIssue_WhenCheckingSelf_ThenUnauthorized` (Task 6).

## Notes for the implementer

**Where the pipeline already refuses callers.** `ActorLivenessFilter` answers `401` when a bearer token names a person who does not exist, is logically deleted, or whose role claim has drifted. A functional test's *caller* must therefore be one of these:
- a seeded, live person whose role matches the token (`TestTokens.For(person.PublicId, role)`);
- one of the fixture's stand-ins (`TestTokens.ForRole(role)`).

`[AllowAnonymous]` endpoints are skipped by the middleware and by every global filter, which is why `GET /api/applications/self` can read and validate its own token.

**Audit targets.** `AuditingCommandHandler` reads the target from the output's `Id`, and a refused command returns no output. Assert on a refusal's audit row by actor and action, not by target. Token issuance has no actor: the caller is anonymous.

**`AsyncFakeRepository` resolves no `Include`.** Unit tests set the navigations they need on the entities they seed: `Application.Scope`, `Application.Owner.ScopeOwnerships` and `ApplicationCredential.Application`. `CreateAsync` keeps an explicit `Id`.

**Signing keys in the functional suite.** `PostgresFixture` sets:
- `HEIMDALL_AUTH_TOKEN_SECRET` to `functional-tests-signing-secret-key`;
- `HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS` to `PostgresFixture.PreviousTokenSecret`;
- issuer and audience to `heimdall-tests`.

Task 2 adds `HEIMDALL_APPLICATION_TOKEN_AUDIENCE=heimdall-tests-applications` beside them, as the constant `PostgresFixture.ApplicationTokenAudience`.

## File map

**Create**
- `src/Domain/ArturRios.Heimdall.Domain/Entities/ApplicationCredential.cs`: the entity, `MaxLive` and the liveness rule (Task 1).
- `src/Domain/ArturRios.Heimdall.Domain/Persistence/IApplicationCredentialSlots.cs`: the locked insert (Task 3).
- `src/Infrastructure/ArturRios.Heimdall.Data/EntityMaps/ApplicationCredentialDbMap.cs` (Task 1).
- `src/Infrastructure/ArturRios.Heimdall.Data/Migrations/<timestamp>_AddApplicationCredential.cs` and `.Designer.cs` (Task 1).
- `src/Infrastructure/ArturRios.Heimdall.Data/Persistence/ApplicationCredentialSlots.cs` (Task 3).
- `src/Application/ArturRios.Heimdall.Command/Services/ApplicationCredentialSecret.cs` (Task 1).
- `src/Application/ArturRios.Heimdall.Command/Services/IApplicationTokenIssuer.cs`: `ApplicationTokenSubject` and `IApplicationTokenIssuer` (Task 2).
- `src/Application/ArturRios.Heimdall.Shared/Security/ApplicationTokenClaims.cs` and `IApplicationTokenValidator.cs` (with `ApplicationTokenPrincipal`) (Task 2).
- `src/Application/ArturRios.Heimdall.Shared/Time/UtcInstant.cs`, moved from Query (Task 3).
- `src/Application/ArturRios.Heimdall.Shared/Services/ApplicationManagementAccess.cs` (Task 3).
- `src/Application/ArturRios.Heimdall.Shared/Messages/ApplicationCredentialMessages.cs` and `ApplicationCredentialMessageMap.cs` (Task 3, extended in Task 4).
- `src/Application/ArturRios.Heimdall.Shared/Messages/ApplicationTokenMessages.cs` and `ApplicationTokenMessageMap.cs` (Task 5, extended in Task 6).
- `src/Application/ArturRios.Heimdall.Command/Input/IssueApplicationCredentialCommand.cs`, `Input/Validation/IssueApplicationCredentialCommandValidator.cs`, `Output/IssueApplicationCredentialCommandOutput.cs` and `Handlers/IssueApplicationCredentialCommandHandler.cs` (Task 3).
- `src/Application/ArturRios.Heimdall.Query/Input/ListApplicationCredentialsQuery.cs`, `Output/ApplicationCredentialOutput.cs`, `Output/ApplicationCredentialListOutput.cs` and `Handlers/ListApplicationCredentialsQueryHandler.cs` (Task 4).
- `src/Application/ArturRios.Heimdall.Command/Input/RevokeApplicationCredentialCommand.cs`, `Output/RevokeApplicationCredentialCommandOutput.cs` and `Handlers/RevokeApplicationCredentialCommandHandler.cs` (Task 4).
- `src/Application/ArturRios.Heimdall.Command/Input/IssueApplicationTokenCommand.cs`, `Input/Validation/IssueApplicationTokenCommandValidator.cs`, `Output/IssueApplicationTokenCommandOutput.cs` and `Handlers/IssueApplicationTokenCommandHandler.cs` (Task 5).
- `src/Application/ArturRios.Heimdall.Query/Input/CheckApplicationTokenQuery.cs`, `Output/ApplicationTokenCheckOutput.cs` and `Handlers/CheckApplicationTokenQueryHandler.cs` (Task 6).
- `src/Presentation/ArturRios.Heimdall.WebApi/Security/ApplicationTokenOptions.cs` and `JwtApplicationTokenService.cs` (Task 2).
- `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/ApplicationCredentialController.cs` (Task 3, extended in Task 4).
- `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/ApplicationTokenController.cs` (Task 5, extended in Task 6).
- Tests:
  - `tests/Domain/ArturRios.Heimdall.Domain.Tests/ApplicationCredentialTests.cs`
  - `tests/Application/ArturRios.Heimdall.Command.Tests/ApplicationCredentialSecretTests.cs`
  - `tests/Application/ArturRios.Heimdall.Command.Tests/Support/InMemoryApplicationCredentialSlots.cs`
  - `tests/Application/ArturRios.Heimdall.Command.Tests/IssueApplicationCredentialCommandHandlerTests.cs`
  - `tests/Application/ArturRios.Heimdall.Command.Tests/IssueApplicationCredentialCommandValidatorTests.cs`
  - `tests/Application/ArturRios.Heimdall.Command.Tests/RevokeApplicationCredentialCommandHandlerTests.cs`
  - `tests/Application/ArturRios.Heimdall.Command.Tests/IssueApplicationTokenCommandHandlerTests.cs`
  - `tests/Application/ArturRios.Heimdall.Command.Tests/IssueApplicationTokenCommandValidatorTests.cs`
  - `tests/Application/ArturRios.Heimdall.Query.Tests/ListApplicationCredentialsQueryHandlerTests.cs`
  - `tests/Application/ArturRios.Heimdall.Query.Tests/CheckApplicationTokenQueryHandlerTests.cs`
  - `tests/Application/ArturRios.Heimdall.Shared.Tests/ApplicationManagementAccessTests.cs`
  - `tests/Application/ArturRios.Heimdall.Shared.Tests/UtcInstantTests.cs` (moved)
  - `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationTokenOptionsTests.cs`
  - `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/JwtApplicationTokenServiceTests.cs`
  - `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationCredentialControllerIssueTests.cs`
  - `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationCredentialControllerListTests.cs`
  - `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationCredentialControllerRevokeTests.cs`
  - `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationTokenControllerIssueTests.cs`
  - `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationTokenControllerSelfTests.cs`
  - `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/Support/ApplicationSeeds.cs`: the seeding helpers the five functional files share (Task 3).
- `docs/content/en/docs/flows/application-authentication.md` (Task 7).
- `api-client/bruno/Applications/`: `Issue a credential.bru`, `List credentials.bru`, `Revoke a credential.bru`, `Obtain an application token.bru` and `Check an application token.bru` (Task 7).

**Modify**
- `src/Domain/ArturRios.Heimdall.Domain/Entities/Application.cs`: the `Credentials` navigation (Task 1).
- `src/Infrastructure/ArturRios.Heimdall.Data/Configuration/AppDbContext.cs`: DbSet and `Configure()` (Task 1).
- `src/Infrastructure/ArturRios.Heimdall.Data/Migrations/AppDbContextModelSnapshot.cs`: generated (Task 1).
- `src/Presentation/ArturRios.Heimdall.WebApi/Security/IdentityUserMapper.cs`: the `tokenKind` refusal (Task 2).
- `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`: options, token service, slots, handlers (Tasks 2 to 6).
- `src/Application/ArturRios.Heimdall.Query/Handlers/ListAuditLogQueryHandler.cs` and `Input/Validation/ListAuditLogQueryValidator.cs`: the `UtcInstant` namespace (Task 3).
- `src/Application/ArturRios.Heimdall.Command/Handlers/PurgeExpiredTokensCommandHandler.cs` and `Output/PurgeExpiredTokensCommandOutput.cs` (Task 4).
- `src/Presentation/ArturRios.Heimdall.WebApi/Environments/.env`, `docker/*.env.example` and `docker-compose.yml` (Task 2).
- Tests:
  - `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/Support/PostgresFixture.cs` (Task 2)
  - `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/SchemaTests.cs` (Task 1)
  - `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/IdentityUserMapperTests.cs` (Task 2)
  - `tests/Application/ArturRios.Heimdall.Command.Tests/PurgeExpiredTokensCommandHandlerTests.cs` (Task 4)
  - `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/TokenRetentionPurgeTests.cs` (Task 4)
- `docs/openapi/heimdall.json`: generated (Tasks 3 to 6).
- Documents and clients (Task 7):
  - `docs/requirements/Use Case Specification Document.md`
  - `docs/requirements/System Requirements Document.md`
  - `docs/requirements/Threat Model Document.md`
  - `docs/requirements/Data Protection Document.md`
  - `docs/requirements/Data Retention Schedule Document.md`
  - `docs/content/en/docs/api-reference.md`
  - `docs/content/en/docs/operations.md`
  - `docs/content/en/docs/getting-started.md`
  - `README.md`
  - `CHANGELOG.md`
  - `api-client/http/applications.http`
  - `api-client/http/http-client.env.json`
  - `api-client/bruno/environments/*.bru`

---

### Task 1: The credential entity, its table and its secret

**Files:**
- Create: `src/Domain/ArturRios.Heimdall.Domain/Entities/ApplicationCredential.cs`
- Modify: `src/Domain/ArturRios.Heimdall.Domain/Entities/Application.cs` (navigation)
- Create: `src/Infrastructure/ArturRios.Heimdall.Data/EntityMaps/ApplicationCredentialDbMap.cs`
- Modify: `src/Infrastructure/ArturRios.Heimdall.Data/Configuration/AppDbContext.cs`
- Create (generated): `src/Infrastructure/ArturRios.Heimdall.Data/Migrations/<timestamp>_AddApplicationCredential.cs`, `.Designer.cs`; Modify (generated): `AppDbContextModelSnapshot.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Services/ApplicationCredentialSecret.cs`
- Test: `tests/Domain/ArturRios.Heimdall.Domain.Tests/ApplicationCredentialTests.cs`, `tests/Application/ArturRios.Heimdall.Command.Tests/ApplicationCredentialSecretTests.cs`, `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/SchemaTests.cs`

**Interfaces:**
- Consumes: `Application` (`Id`, `PublicId`, `Name`, `IsDeleted`, `ScopeId`, `Scope`), `SingleUseTokenHash.Of(string)`.
- Produces:
  - `ApplicationCredential : Entity<long>` with `Guid PublicId`, `long ApplicationId`, `string SecretHash`, `DateTime CreatedAt`, `DateTime? ExpiresAt`, `DateTime? RevokedAt`, `DateTime? LastUsedAt`, `Application Application`; `const int MaxLive = 2`; `static Expression<Func<ApplicationCredential, bool>> LiveAt(DateTime now)`; `bool IsLiveAt(DateTime now)`.
  - `Application.Credentials` (`ICollection<ApplicationCredential>`).
  - `AppDbContext.ApplicationCredentials`.
  - `ApplicationCredentialSecret.Prefix` (`"hds_"`), `ApplicationCredentialSecret.New()`, `ApplicationCredentialSecret.HashOf(string secret)`.

- [ ] **Step 1: Write the failing liveness tests**

Create `tests/Domain/ArturRios.Heimdall.Domain.Tests/ApplicationCredentialTests.cs`:

```csharp
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Domain.Tests;

// Unit tests for ApplicationCredential's liveness rule (UC-53…UC-57, FR-AP-10): a credential is live
// while it is not revoked and either has no expiry or expires after the moment asked about. The rule
// is written once, as an expression EF can translate, and IsLiveAt evaluates that same expression in
// memory, so these cases pin both.
public class ApplicationCredentialTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    [UnitFact]
    public void GivenNoExpiryAndNotRevoked_WhenAskedIfLive_ThenItIs()
    {
        // Given
        var credential = new ApplicationCredential();

        // When
        var live = credential.IsLiveAt(Now);

        // Then
        Assert.True(live);
    }

    [UnitFact]
    public void GivenAnExpiryAfterNow_WhenAskedIfLive_ThenItIs()
    {
        // Given
        var credential = new ApplicationCredential { ExpiresAt = Now.AddSeconds(1) };

        // When
        var live = credential.IsLiveAt(Now);

        // Then
        Assert.True(live);
    }

    [UnitFact]
    public void GivenAnExpiryAtExactlyNow_WhenAskedIfLive_ThenItIsNot()
    {
        // Given — the boundary belongs to "expired", as for every token in this API
        var credential = new ApplicationCredential { ExpiresAt = Now };

        // When
        var live = credential.IsLiveAt(Now);

        // Then
        Assert.False(live);
    }

    [UnitFact]
    public void GivenARevokedCredentialWithoutExpiry_WhenAskedIfLive_ThenItIsNot()
    {
        // Given
        var credential = new ApplicationCredential { RevokedAt = Now.AddDays(-1) };

        // When
        var live = credential.IsLiveAt(Now);

        // Then
        Assert.False(live);
    }

    [UnitFact]
    public void GivenSeveralCredentials_WhenFilteredByTheExpression_ThenOnlyLiveOnesRemain()
    {
        // Given
        var live = new ApplicationCredential { ExpiresAt = Now.AddHours(1) };
        var expired = new ApplicationCredential { ExpiresAt = Now.AddHours(-1) };
        var revoked = new ApplicationCredential { RevokedAt = Now.AddHours(-1) };

        // When
        var remaining = new[] { live, expired, revoked }.AsQueryable()
            .Where(ApplicationCredential.LiveAt(Now))
            .ToList();

        // Then
        Assert.Equal([live], remaining);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build src/ArturRios.Heimdall.sln` — Expected: build FAILS, `ApplicationCredential` not found.

- [ ] **Step 3: Write the entity and the navigation**

Create `src/Domain/ArturRios.Heimdall.Domain/Entities/ApplicationCredential.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A secret an <see cref="Application" /> exchanges for an application token (UC-53, UC-56,
///     FR-AP-10). Only the secret's SHA-256 is stored; the secret itself is shown once, in UC-53's
///     response, and never again.
/// </summary>
/// <remarks>
///     <para>
///         An application holds at most <see cref="MaxLive" /> live credentials, so a new one can be
///         deployed before the old one is revoked (FR-AP-11). Each is revoked on its own (UC-55) and
///         may carry an expiry.
///     </para>
///     <para>
///         No personal data: the row names an application, a hash and four instants. Logically
///         deleting the application or its scope leaves the rows alone — UC-56 and UC-57 refuse a
///         deleted application or scope, so its credentials are inert — and hard-deleting the
///         application removes them by cascade. Revoked and expired rows are purged after the
///         single-use-token grace by the token retention pass.
///     </para>
/// </remarks>
public class ApplicationCredential : Entity<long>
{
    /// <summary>The most live credentials one application may hold at once (FR-AP-11).</summary>
    public const int MaxLive = 2;

    /// <summary>
    ///     External identifier — the credential's reference in UC-54's listing, UC-55's route, the
    ///     token's <c>credentialId</c> claim and the audit trail. The internal
    ///     <see cref="Entity{TKey}.Id" /> is never exposed.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Foreign key to the owning <see cref="Application" /> (internal Id). Required.</summary>
    public long ApplicationId { get; set; }

    /// <summary>
    ///     SHA-256 of the secret, as lowercase hex (<c>ApplicationCredentialSecret.HashOf</c>). The
    ///     secret itself is never stored: whoever could read this table could otherwise authenticate
    ///     as every application (Threat Model TH-33).
    /// </summary>
    [MaxLength(64)]
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>When the credential was issued (UC-53).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the credential stops being accepted, or <c>null</c> for never.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>When the credential was revoked (UC-55), or <c>null</c> while it has not been.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>When a token was last issued from this credential (UC-56), or <c>null</c> if never.</summary>
    public DateTime? LastUsedAt { get; set; }

    // Navigation properties

    /// <summary>The application this credential authenticates.</summary>
    public Application Application { get; set; } = null!;

    /// <summary>
    ///     The one definition of a live credential — not revoked, and either without expiry or
    ///     expiring after <paramref name="now" /> — as an expression, so a query can filter on it in
    ///     the database.
    /// </summary>
    public static Expression<Func<ApplicationCredential, bool>> LiveAt(DateTime now) =>
        credential => credential.RevokedAt == null &&
                      (credential.ExpiresAt == null || credential.ExpiresAt > now);

    /// <summary><see cref="LiveAt" />, evaluated for this instance.</summary>
    public bool IsLiveAt(DateTime now) => LiveAt(now).Compile()(this);
}
```

In `src/Domain/ArturRios.Heimdall.Domain/Entities/Application.cs`, after the `Owner` navigation, add:

```csharp
    /// <summary>The secrets this application can exchange for an application token (UC-53).</summary>
    public ICollection<ApplicationCredential> Credentials { get; set; } = new List<ApplicationCredential>();
```

- [ ] **Step 4: Run the liveness tests to verify they pass**

Run: `dotnet build src/ArturRios.Heimdall.sln && dotnet test tests/Domain/ArturRios.Heimdall.Domain.Tests --no-build`
Expected: PASS (5 tests).

- [ ] **Step 5: Write the failing secret tests**

Create `tests/Application/ArturRios.Heimdall.Command.Tests/ApplicationCredentialSecretTests.cs`:

```csharp
using ArturRios.Heimdall.Command.Services;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for ApplicationCredentialSecret (UC-53, TH-33): a secret is `hds_` plus 32 random bytes,
// base64url-encoded without padding, so it is recognisable in a leaked log and safe in a URL or an
// environment file; it is stored only as its SHA-256.
public class ApplicationCredentialSecretTests
{
    [UnitFact]
    public void GivenANewSecret_WhenInspected_ThenItIsPrefixedAndCarriesThirtyTwoBytes()
    {
        // Given / When
        var secret = ApplicationCredentialSecret.New();

        // Then — 4 prefix characters + 43 base64url characters for 32 bytes
        Assert.StartsWith(ApplicationCredentialSecret.Prefix, secret);
        Assert.Equal(47, secret.Length);
        Assert.Matches("^hds_[A-Za-z0-9_-]{43}$", secret);
    }

    [UnitFact]
    public void GivenTwoNewSecrets_WhenCompared_ThenTheyDiffer()
    {
        // Given / When
        var first = ApplicationCredentialSecret.New();
        var second = ApplicationCredentialSecret.New();

        // Then
        Assert.NotEqual(first, second);
    }

    [UnitFact]
    public void GivenASecret_WhenHashed_ThenItIsLowercaseSha256HexAndStable()
    {
        // Given
        var secret = ApplicationCredentialSecret.New();

        // When
        var hash = ApplicationCredentialSecret.HashOf(secret);

        // Then
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, ApplicationCredentialSecret.HashOf(secret));
        Assert.NotEqual(hash, ApplicationCredentialSecret.HashOf(ApplicationCredentialSecret.New()));
    }
}
```

- [ ] **Step 6: Run them to verify they fail**

Run: `dotnet build src/ArturRios.Heimdall.sln` — Expected: build FAILS, `ApplicationCredentialSecret` not found.

- [ ] **Step 7: Write the secret helper**

Create `src/Application/ArturRios.Heimdall.Command/Services/ApplicationCredentialSecret.cs`:

```csharp
using System.Security.Cryptography;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Generates and hashes the secrets of UC-53 (FR-AP-10, Threat Model TH-33).
/// </summary>
/// <remarks>
///     <para>
///         <b>Shape.</b> <see cref="Prefix" /> plus 32 bytes from a cryptographic generator,
///         base64url-encoded without padding. The prefix makes a leaked secret recognisable — to a
///         person reading a log and to a secret scanner — and base64url keeps it safe in a URL, a
///         header or an environment file.
///     </para>
///     <para>
///         <b>Why a fast hash.</b> 256 random bits cannot be guessed, so a slow password hash buys
///         nothing and would put UC-56 behind the Argon2id gate the password logins share (NFR-18).
///         A credential is found by looking its hash up, so no comparison runs whose duration
///         depends on how much of a submitted secret matches.
///     </para>
/// </remarks>
public static class ApplicationCredentialSecret
{
    /// <summary>The prefix every secret starts with.</summary>
    public const string Prefix = "hds_";

    private const int SecretBytes = 32;

    /// <summary>A new secret, shown to the issuer once and never stored.</summary>
    public static string New() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>The value stored in <c>ApplicationCredential.SecretHash</c> and looked up by UC-56.</summary>
    public static string HashOf(string secret) => SingleUseTokenHash.Of(secret);
}
```

- [ ] **Step 8: Run the secret tests to verify they pass**

Run: `dotnet build src/ArturRios.Heimdall.sln && dotnet test tests/Application/ArturRios.Heimdall.Command.Tests --no-build --filter "FullyQualifiedName~ApplicationCredentialSecretTests"`
Expected: PASS (3 tests).

- [ ] **Step 9: Write the failing schema test**

In `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/SchemaTests.cs`, add `"application_credential",` to `expected` in `GivenMigrationsApplied_WhenSchemaInspected_ThenTablesAreSingularSnakeCase`, directly after `"application",`. Then add, at the end of the class, a test that the hash is unique and the public id indexed (follow `GivenMigrationsApplied_WhenAuditIndexesInspected_ThenTargetIdIsIndexed`, reading `pg_indexes` the same way):

```csharp
    [FunctionalFact]
    public async Task GivenMigrationsApplied_WhenCredentialIndexesInspected_ThenHashAndPublicIdAreUnique()
    {
        // Given the migrated schema

        // When
        await using var context = db.CreateContext();
        var indexes = await context.Database
            .SqlQueryRaw<string>(
                "SELECT indexdef AS \"Value\" FROM pg_indexes " +
                "WHERE schemaname = 'heimdall' AND tablename = 'application_credential'")
            .ToListAsync();

        // Then — UC-56 finds a credential by its hash, and UC-55 by its public id
        Assert.Contains(indexes, definition =>
            definition.Contains("UNIQUE") && definition.Contains("(secret_hash)"));
        Assert.Contains(indexes, definition =>
            definition.Contains("UNIQUE") && definition.Contains("(public_id)"));
    }
```

If the existing audit-index test reads `pg_indexes` differently (a helper method, a different projection), use its approach instead and keep these two assertions.

- [ ] **Step 10: Write the map, the DbSet and the migration**

Create `src/Infrastructure/ArturRios.Heimdall.Data/EntityMaps/ApplicationCredentialDbMap.cs`:

```csharp
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class ApplicationCredentialDbMap
{
    public static void Configure(this EntityTypeBuilder<ApplicationCredential> credential)
    {
        credential.ToTable("application_credential");

        credential.HasKey(x => x.Id);

        credential.Property(x => x.PublicId).IsRequired();
        credential.HasIndex(x => x.PublicId).IsUnique();

        // UC-56 looks a credential up by the hash of the presented secret, so the hash is indexed;
        // unique because two credentials sharing one would make that lookup ambiguous.
        credential.Property(x => x.SecretHash).IsRequired();
        credential.HasIndex(x => x.SecretHash).IsUnique();

        credential.Property(x => x.CreatedAt).IsRequired();

        // Credentials go with their application when it is hard-deleted (UC-20); a logical delete
        // leaves them, and UC-56/UC-57 refuse them through the application's flag.
        credential.HasOne(x => x.Application)
            .WithMany(a => a.Credentials)
            .HasForeignKey(x => x.ApplicationId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

In `src/Infrastructure/ArturRios.Heimdall.Data/Configuration/AppDbContext.cs`, add `public DbSet<ApplicationCredential> ApplicationCredentials { get; init; }` after `Applications`, and `modelBuilder.Entity<ApplicationCredential>().Configure();` after `modelBuilder.Entity<Application>().Configure();`.

Generate the migration from the worktree root:

```bash
dotnet build src/ArturRios.Heimdall.sln
dotnet tool restore
HEIMDALL_DATA_CONNECTIONSTRING="Host=localhost;Port=5432;Database=heimdall;Username=postgres;Password=postgres" \
  dotnet ef migrations add AddApplicationCredential \
  --project src/Infrastructure/ArturRios.Heimdall.Data \
  --startup-project src/Infrastructure/ArturRios.Heimdall.Data \
  --output-dir Migrations
```

Expected: one `CreateTable("application_credential", …)` with the foreign key to `application` (cascade) and three indexes (`application_id`, `public_id` unique, `secret_hash` unique). Nothing else may change; if the snapshot diff touches other tables, stop and investigate. Replace the class's `/// <inheritdoc />` with:

```csharp
    /// <summary>
    ///     Creates APPLICATION_CREDENTIAL (UC-53…UC-57, FR-AP-10): the hashed secrets an application
    ///     exchanges for an application token.
    /// </summary>
    /// <remarks>
    ///     A new table with no existing rows, so nothing is backfilled. It holds no personal data —
    ///     an application reference, a SHA-256 and four instants — which the Data Protection
    ///     Document records; the Data Retention Schedule's token row covers its purge.
    /// </remarks>
```

- [ ] **Step 11: Run the schema tests and the model check**

Run:
```bash
dotnet build src/ArturRios.Heimdall.sln
dotnet test tests/Presentation/ArturRios.Heimdall.WebApi.Tests --no-build --filter "FullyQualifiedName~SchemaTests"
HEIMDALL_DATA_CONNECTIONSTRING="Host=localhost;Port=5432;Database=heimdall;Username=postgres;Password=postgres" \
  dotnet ef migrations has-pending-model-changes --project src/Infrastructure/ArturRios.Heimdall.Data \
  --startup-project src/Infrastructure/ArturRios.Heimdall.Data --no-build
```
Expected: SchemaTests PASS; "No changes have been made to the model since the last migration."

- [ ] **Step 12: Run the full Domain, Command and Data test projects, one at a time**

Run each, sequentially: `dotnet test tests/Domain/ArturRios.Heimdall.Domain.Tests --no-build`, `dotnet test tests/Application/ArturRios.Heimdall.Command.Tests --no-build`, `dotnet test tests/Infrastructure/ArturRios.Heimdall.Data.Tests --no-build`. Expected: all PASS.

- [ ] **Step 13: Commit**

```bash
git add src/Domain/ArturRios.Heimdall.Domain/Entities/ApplicationCredential.cs \
  src/Domain/ArturRios.Heimdall.Domain/Entities/Application.cs \
  src/Infrastructure/ArturRios.Heimdall.Data/EntityMaps/ApplicationCredentialDbMap.cs \
  src/Infrastructure/ArturRios.Heimdall.Data/Configuration/AppDbContext.cs \
  src/Infrastructure/ArturRios.Heimdall.Data/Migrations \
  src/Application/ArturRios.Heimdall.Command/Services/ApplicationCredentialSecret.cs \
  tests/Domain/ArturRios.Heimdall.Domain.Tests/ApplicationCredentialTests.cs \
  tests/Application/ArturRios.Heimdall.Command.Tests/ApplicationCredentialSecretTests.cs \
  tests/Presentation/ArturRios.Heimdall.WebApi.Tests/SchemaTests.cs
git commit -m "feat: add the application credential table" -m "..."
```

Body: what the table holds, that only the hash is stored, and the migration name; then the two trailer lines.

---

### Task 2: Application token configuration, signing and validation

**Files:**
- Create: `src/Application/ArturRios.Heimdall.Shared/Security/ApplicationTokenClaims.cs`, `src/Application/ArturRios.Heimdall.Shared/Security/IApplicationTokenValidator.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Services/IApplicationTokenIssuer.cs`
- Create: `src/Presentation/ArturRios.Heimdall.WebApi/Security/ApplicationTokenOptions.cs`, `src/Presentation/ArturRios.Heimdall.WebApi/Security/JwtApplicationTokenService.cs`
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Security/IdentityUserMapper.cs`, `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`
- Modify: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/Support/PostgresFixture.cs`
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Environments/.env`, `docker/local.env.example`, `docker/development.env.example`, `docker/homologation.env.example`, `docker/production.env.example`, `docker-compose.yml`
- Test: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationTokenOptionsTests.cs`, `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/JwtApplicationTokenServiceTests.cs`, `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/IdentityUserMapperTests.cs`

**Interfaces:**
- Consumes: `JwtConfiguration` (`Issuer`, `Audience`, `Secret`, `Keys`, `ExpirationInSeconds`, `Claims`), `JwtHandler.CreateToken`, `AuthToken(string Token, DateTime ExpiresAt)` (`Command/Services/IAuthTokenIssuer.cs`).
- Produces:
  - `ApplicationTokenClaims` (`TokenKind`, `ApplicationKind`, `Id`, `ScopeId`, `CredentialId`, `Name`).
  - `record ApplicationTokenPrincipal(Guid ApplicationId, Guid ScopeId, Guid CredentialId, DateTime ExpiresAt)` and `IApplicationTokenValidator.ValidateAsync(string? token) → Task<ApplicationTokenPrincipal?>` (namespace `ArturRios.Heimdall.Shared.Security`).
  - `record ApplicationTokenSubject(Guid ApplicationId, Guid ScopeId, Guid CredentialId, string Name)` and `IApplicationTokenIssuer.IssueAsync(ApplicationTokenSubject) → Task<AuthToken>` (namespace `ArturRios.Heimdall.Command.Services`).
  - `ApplicationTokenOptions(string Audience, TimeSpan Lifetime)` with `AudienceVariable`, `ExpirationVariable`, `DefaultLifetime`, `FromEnvironment()`, `Parse(string?, string?, string?)`.
  - `JwtApplicationTokenService` registered scoped and exposed as both interfaces.
  - `PostgresFixture.ApplicationTokenAudience` (`"heimdall-tests-applications"`).

- [ ] **Step 1: Write the failing options tests**

Create `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationTokenOptionsTests.cs`:

```csharp
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for ApplicationTokenOptions (UC-56, TH-35): the application audience is required and
// must differ from the person audience — the separation is what stops a consumer that pins the
// person audience from taking an application token for a person — and the lifetime defaults to ten
// minutes. Start-up refuses a missing or equal audience instead of running without the separation.
public class ApplicationTokenOptionsTests
{
    [UnitFact]
    public void GivenADistinctAudienceAndNoLifetime_WhenParsed_ThenTheLifetimeIsTenMinutes()
    {
        // Given / When
        var options = ApplicationTokenOptions.Parse("heimdall-applications", null, "heimdall");

        // Then
        Assert.Equal("heimdall-applications", options.Audience);
        Assert.Equal(TimeSpan.FromMinutes(10), options.Lifetime);
    }

    [UnitFact]
    public void GivenALifetimeInSeconds_WhenParsed_ThenItIsUsed()
    {
        // Given / When
        var options = ApplicationTokenOptions.Parse("heimdall-applications", "300", "heimdall");

        // Then
        Assert.Equal(TimeSpan.FromMinutes(5), options.Lifetime);
    }

    [UnitTheory]
    [InlineData("10m")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("86401")]
    [InlineData("NaN")]
    public void GivenAnUnusableLifetime_WhenParsed_ThenTheDefaultApplies(string raw)
    {
        // Given / When
        var options = ApplicationTokenOptions.Parse("heimdall-applications", raw, "heimdall");

        // Then
        Assert.Equal(ApplicationTokenOptions.DefaultLifetime, options.Lifetime);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GivenNoAudience_WhenParsed_ThenStartUpIsRefused(string? audience)
    {
        // Given / When
        var error = Assert.Throws<InvalidOperationException>(
            () => ApplicationTokenOptions.Parse(audience, null, "heimdall"));

        // Then
        Assert.Contains(ApplicationTokenOptions.AudienceVariable, error.Message);
    }

    [UnitTheory]
    [InlineData("heimdall")]
    [InlineData(" heimdall ")]
    public void GivenTheSameAudienceAsPersonTokens_WhenParsed_ThenStartUpIsRefused(string audience)
    {
        // Given / When
        var error = Assert.Throws<InvalidOperationException>(
            () => ApplicationTokenOptions.Parse(audience, null, "heimdall"));

        // Then
        Assert.Contains(ApplicationTokenOptions.AudienceVariable, error.Message);
        Assert.Contains("HEIMDALL_AUTH_TOKEN_AUDIENCE", error.Message);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build src/ArturRios.Heimdall.sln` — Expected: build FAILS, `ApplicationTokenOptions` not found.

- [ ] **Step 3: Write the options**

Create `src/Presentation/ArturRios.Heimdall.WebApi/Security/ApplicationTokenOptions.cs`:

```csharp
using System.Globalization;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     The audience and lifetime of application tokens (UC-56, FR-AP-13), read once at start-up.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the audience is required and distinct.</b> Application tokens are signed with the
///         person tokens' key pair. A consumer that validates person tokens offline would take an
///         application token for a person if both carried one audience; a distinct audience is what
///         makes it refuse (Threat Model TH-35). So start-up refuses a missing audience, and one equal
///         to <c>HEIMDALL_AUTH_TOKEN_AUDIENCE</c>, rather than run without the separation.
///     </para>
///     <para>
///         <b>Lifetime.</b> Ten minutes by default (TH-34): short enough that a leaked token is soon
///         worthless, while UC-57's live re-check makes a revocation effective at once regardless. An
///         unusable value falls back to the default rather than stopping start-up, as
///         <c>HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS</c> does.
///     </para>
/// </remarks>
/// <param name="Audience">The <c>aud</c> every application token carries and UC-57 requires.</param>
/// <param name="Lifetime">How long an application token is valid after issue.</param>
public sealed record ApplicationTokenOptions(string Audience, TimeSpan Lifetime)
{
    /// <summary>The variable naming the application token audience. Required.</summary>
    public const string AudienceVariable = "HEIMDALL_APPLICATION_TOKEN_AUDIENCE";

    /// <summary>The variable carrying the lifetime in seconds. Optional.</summary>
    public const string ExpirationVariable = "HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS";

    private const string PersonAudienceVariable = "HEIMDALL_AUTH_TOKEN_AUDIENCE";

    private const double MaxLifetimeInSeconds = 86400;

    /// <summary>The lifetime used when none, or an unusable one, is configured.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Reads the options from the environment; throws when the audience is unusable.</summary>
    public static ApplicationTokenOptions FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(AudienceVariable),
        Environment.GetEnvironmentVariable(ExpirationVariable),
        Environment.GetEnvironmentVariable(PersonAudienceVariable));

    /// <summary>
    ///     Builds the options from raw values. Throws <see cref="InvalidOperationException" /> when
    ///     <paramref name="audience" /> is blank or equals <paramref name="personAudience" />.
    /// </summary>
    public static ApplicationTokenOptions Parse(string? audience, string? expirationInSeconds, string? personAudience)
    {
        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new InvalidOperationException(
                $"Environment variable '{AudienceVariable}' is unset. Application tokens need an " +
                $"audience of their own, different from '{PersonAudienceVariable}'.");
        }

        var trimmed = audience.Trim();

        if (string.Equals(trimmed, personAudience?.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Environment variable '{AudienceVariable}' equals '{PersonAudienceVariable}'. An " +
                "application token must carry an audience person tokens do not, or a consumer " +
                "validating person tokens would accept it as one.");
        }

        var lifetime = double.TryParse(
                           expirationInSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                       double.IsFinite(seconds) && seconds > 0 && seconds <= MaxLifetimeInSeconds
            ? TimeSpan.FromSeconds(seconds)
            : DefaultLifetime;

        return new ApplicationTokenOptions(trimmed, lifetime);
    }
}
```

- [ ] **Step 4: Run the options tests to verify they pass**

Run: `dotnet build src/ArturRios.Heimdall.sln && dotnet test tests/Presentation/ArturRios.Heimdall.WebApi.Tests --no-build --filter "FullyQualifiedName~ApplicationTokenOptionsTests"`
Expected: PASS (12 cases).

- [ ] **Step 5: Write the failing token service tests**

Create `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/JwtApplicationTokenServiceTests.cs`:

```csharp
using System.Text;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Jwt;
using ArturRios.Util.Test.Attributes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for JwtApplicationTokenService (UC-56, UC-57, TH-34, TH-35): the token it issues carries
// the application claims and no role, is accepted by its own validator under the current or the
// previous key, and is refused for every other audience, kind, signature or lifetime. Negative cases
// mint their tokens directly, so each varies exactly one thing.
public class JwtApplicationTokenServiceTests
{
    private const string Issuer = "heimdall-unit";
    private const string PersonAudience = "heimdall-unit";
    private const string ApplicationAudience = "heimdall-unit-applications";
    private const string CurrentSecret = "unit-tests-current-signing-secret-0123456789";
    private const string PreviousSecret = "unit-tests-previous-signing-secret-0123456789";

    private static readonly ApplicationTokenSubject Subject =
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Billing Service");

    private static JwtConfiguration Configuration(string issuer = Issuer) =>
        new(3600, issuer, PersonAudience, CurrentSecret, [])
        {
            Keys = [new JwtKey("current", CurrentSecret), new JwtKey("previous", PreviousSecret)]
        };

    private static JwtApplicationTokenService Service(string issuer = Issuer) =>
        new(Configuration(issuer), new JwtHandler(),
            new ApplicationTokenOptions(ApplicationAudience, TimeSpan.FromMinutes(10)));

    private static Dictionary<string, object> ApplicationClaims() => new()
    {
        [ApplicationTokenClaims.TokenKind] = ApplicationTokenClaims.ApplicationKind,
        [ApplicationTokenClaims.Id] = Subject.ApplicationId.ToString(),
        [ApplicationTokenClaims.ScopeId] = Subject.ScopeId.ToString(),
        [ApplicationTokenClaims.CredentialId] = Subject.CredentialId.ToString(),
        [ApplicationTokenClaims.Name] = Subject.Name
    };

    private static string Mint(
        IDictionary<string, object> claims,
        string audience = ApplicationAudience,
        string secret = CurrentSecret,
        DateTime? expires = null,
        string issuer = Issuer)
    {
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(10);

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            NotBefore = expiry.AddMinutes(-20),
            IssuedAt = expiry.AddMinutes(-20),
            Expires = expiry,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secret)), SecurityAlgorithms.HmacSha256)
        });
    }

    [UnitFact]
    public async Task GivenASubject_WhenIssuing_ThenTheTokenCarriesTheApplicationClaimsAndNoRole()
    {
        // Given
        var service = Service();

        // When
        var issued = await service.IssueAsync(Subject);

        // Then
        var token = new JsonWebToken(issued.Token);
        Assert.Equal(ApplicationTokenClaims.ApplicationKind, token.GetClaim(ApplicationTokenClaims.TokenKind).Value);
        Assert.Equal(Subject.ApplicationId.ToString(), token.GetClaim(ApplicationTokenClaims.Id).Value);
        Assert.Equal(Subject.ScopeId.ToString(), token.GetClaim(ApplicationTokenClaims.ScopeId).Value);
        Assert.Equal(Subject.CredentialId.ToString(), token.GetClaim(ApplicationTokenClaims.CredentialId).Value);
        Assert.Equal(Subject.Name, token.GetClaim(ApplicationTokenClaims.Name).Value);
        Assert.Equal([ApplicationAudience], token.Audiences);
        Assert.False(token.TryGetClaim("roleId", out _));
        Assert.InRange(issued.ExpiresAt, DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow.AddMinutes(11));
    }

    [UnitFact]
    public async Task GivenAnIssuedToken_WhenValidated_ThenItsPrincipalIsReturned()
    {
        // Given
        var service = Service();
        var issued = await service.IssueAsync(Subject);

        // When
        var principal = await service.ValidateAsync(issued.Token);

        // Then
        Assert.NotNull(principal);
        Assert.Equal(Subject.ApplicationId, principal.ApplicationId);
        Assert.Equal(Subject.ScopeId, principal.ScopeId);
        Assert.Equal(Subject.CredentialId, principal.CredentialId);
    }

    [UnitFact]
    public async Task GivenNoIssuerConfigured_WhenRoundTripping_ThenTheTokenIsAccepted()
    {
        // Given — HEIMDALL_AUTH_TOKEN_ISSUER defaults to empty, and an empty ValidIssuer refuses all
        var service = Service(issuer: string.Empty);
        var issued = await service.IssueAsync(Subject);

        // When
        var principal = await service.ValidateAsync(issued.Token);

        // Then
        Assert.NotNull(principal);
    }

    [UnitFact]
    public async Task GivenATokenSignedWithThePreviousKey_WhenValidated_ThenItIsAccepted()
    {
        // Given — signed before a rotation (TH-22)
        var token = Mint(ApplicationClaims(), secret: PreviousSecret);

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.NotNull(principal);
    }

    [UnitFact]
    public async Task GivenATokenSignedWithAnUnknownKey_WhenValidated_ThenItIsRefused()
    {
        // Given
        var token = Mint(ApplicationClaims(), secret: "some-other-signing-secret-0123456789abcdef");

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitFact]
    public async Task GivenThePersonAudience_WhenValidated_ThenItIsRefused()
    {
        // Given — every application claim, but minted for person consumers
        var token = Mint(ApplicationClaims(), audience: PersonAudience);

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitFact]
    public async Task GivenAnotherIssuer_WhenValidated_ThenItIsRefused()
    {
        // Given
        var token = Mint(ApplicationClaims(), issuer: "someone-else");

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitFact]
    public async Task GivenNoApplicationKind_WhenValidated_ThenItIsRefused()
    {
        // Given — right audience and key, but not an application token
        var claims = ApplicationClaims();
        claims.Remove(ApplicationTokenClaims.TokenKind);
        var token = Mint(claims);

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitFact]
    public async Task GivenAnExpiredToken_WhenValidated_ThenItIsRefusedWithoutClockSkew()
    {
        // Given — expired five seconds ago, well inside the default five-minute skew
        var token = Mint(ApplicationClaims(), expires: DateTime.UtcNow.AddSeconds(-5));

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public async Task GivenGarbage_WhenValidated_ThenItIsRefused(string? token)
    {
        // Given / When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitFact]
    public async Task GivenAnUnreadableId_WhenValidated_ThenItIsRefused()
    {
        // Given
        var claims = ApplicationClaims();
        claims[ApplicationTokenClaims.CredentialId] = "not-a-guid";
        var token = Mint(claims);

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }
}
```

- [ ] **Step 6: Run them to verify they fail**

Run: `dotnet build src/ArturRios.Heimdall.sln` — Expected: build FAILS on the missing types.

- [ ] **Step 7: Write the claims, the interfaces and the service**

Create `src/Application/ArturRios.Heimdall.Shared/Security/ApplicationTokenClaims.cs`:

```csharp
namespace ArturRios.Heimdall.Shared.Security;

/// <summary>
///     The claims an application token carries (UC-56, FR-AP-13). Consumers read them after
///     validating the token offline; the consumer guide lists them.
/// </summary>
public static class ApplicationTokenClaims
{
    /// <summary>
    ///     Marks the token's kind. Present with <see cref="ApplicationKind" /> on every application
    ///     token and on no person token, so <c>IdentityUserMapper</c> refuses any token carrying it.
    /// </summary>
    public const string TokenKind = "tokenKind";

    /// <summary>The value of <see cref="TokenKind" /> on an application token.</summary>
    public const string ApplicationKind = "application";

    /// <summary>The application's <c>PublicId</c>.</summary>
    public const string Id = "id";

    /// <summary>The <c>PublicId</c> of the application's scope.</summary>
    public const string ScopeId = "scopeId";

    /// <summary>The <c>PublicId</c> of the credential the token was issued from.</summary>
    public const string CredentialId = "credentialId";

    /// <summary>The application's name when the token was issued.</summary>
    public const string Name = "name";
}
```

Create `src/Application/ArturRios.Heimdall.Shared/Security/IApplicationTokenValidator.cs`:

```csharp
namespace ArturRios.Heimdall.Shared.Security;

/// <summary>What a valid application token says, before the database is consulted (UC-57).</summary>
/// <param name="ApplicationId">The application's <c>PublicId</c>.</param>
/// <param name="ScopeId">Its scope's <c>PublicId</c>.</param>
/// <param name="CredentialId">The issuing credential's <c>PublicId</c>.</param>
/// <param name="ExpiresAt">When the token expires (UTC).</param>
public sealed record ApplicationTokenPrincipal(Guid ApplicationId, Guid ScopeId, Guid CredentialId, DateTime ExpiresAt);

/// <summary>
///     Validates an application token offline — signature against the current and previous keys,
///     issuer, the application audience, lifetime with no clock skew, and the application kind
///     (UC-57, TH-35). Whether the application, scope and credential are still live is the caller's
///     question, answered against the database.
/// </summary>
public interface IApplicationTokenValidator
{
    /// <param name="token">The bearer token presented to <c>GET /api/applications/self</c>.</param>
    /// <returns>The principal, or <see langword="null" /> when the token is not a valid application token.</returns>
    Task<ApplicationTokenPrincipal?> ValidateAsync(string? token);
}
```

Create `src/Application/ArturRios.Heimdall.Command/Services/IApplicationTokenIssuer.cs`:

```csharp
namespace ArturRios.Heimdall.Command.Services;

/// <summary>Who an application token is issued to (UC-56).</summary>
/// <param name="ApplicationId">The application's <c>PublicId</c>.</param>
/// <param name="ScopeId">Its scope's <c>PublicId</c>.</param>
/// <param name="CredentialId">The <c>PublicId</c> of the credential that was presented.</param>
/// <param name="Name">The application's name.</param>
public sealed record ApplicationTokenSubject(Guid ApplicationId, Guid ScopeId, Guid CredentialId, string Name);

/// <summary>Issues UC-56's application token (FR-AP-13).</summary>
public interface IApplicationTokenIssuer
{
    /// <returns>The signed token and when it expires.</returns>
    Task<AuthToken> IssueAsync(ApplicationTokenSubject subject);
}
```

Create `src/Presentation/ArturRios.Heimdall.WebApi/Security/JwtApplicationTokenService.cs`:

```csharp
using System.Text;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Jwt;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Issues and validates application tokens (UC-56, UC-57, FR-AP-13): HMAC-SHA256, signed with
///     the same key pair as person tokens, under the application audience.
/// </summary>
/// <remarks>
///     <para>
///         <b>Signing</b> goes through <see cref="JwtHandler" /> with the person configuration, so the
///         current key signs and a rotation (<c>HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS</c>) covers
///         application tokens exactly as it covers person tokens. Only the audience, the lifetime and
///         the claims differ. No <c>roleId</c> is written: an application holds no Heimdall role.
///     </para>
///     <para>
///         <b>Validating</b> cannot go through <see cref="JwtHandler.IsTokenValidAsync(string, IEnumerable{JwtKey})" />,
///         which checks only the signature and lifetime. Here the issuer, the audience and the kind
///         are the point (TH-35), so the token is validated with every parameter set and no clock
///         skew. Keys are read as ASCII, as <c>ArturRios.Jwt</c> writes them. The issuer is checked
///         only when one is configured: <c>HEIMDALL_AUTH_TOKEN_ISSUER</c> defaults to empty, and
///         an empty expected issuer would refuse every token.
///     </para>
/// </remarks>
public class JwtApplicationTokenService(
    JwtConfiguration configuration,
    JwtHandler jwtHandler,
    ApplicationTokenOptions options) : IApplicationTokenIssuer, IApplicationTokenValidator
{
    private static readonly JsonWebTokenHandler Reader = new();

    public Task<AuthToken> IssueAsync(ApplicationTokenSubject subject)
    {
        var claims = new Dictionary<string, string>
        {
            [ApplicationTokenClaims.TokenKind] = ApplicationTokenClaims.ApplicationKind,
            [ApplicationTokenClaims.Id] = subject.ApplicationId.ToString(),
            [ApplicationTokenClaims.ScopeId] = subject.ScopeId.ToString(),
            [ApplicationTokenClaims.CredentialId] = subject.CredentialId.ToString(),
            [ApplicationTokenClaims.Name] = subject.Name
        };

        var tokenConfiguration = configuration with
        {
            Audience = options.Audience,
            ExpirationInSeconds = options.Lifetime.TotalSeconds,
            Claims = claims
        };

        return Task.FromResult(new AuthToken(
            jwtHandler.CreateToken(tokenConfiguration),
            DateTime.UtcNow.Add(options.Lifetime)));
    }

    public async Task<ApplicationTokenPrincipal?> ValidateAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var result = await Reader.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = configuration.Issuer,
            ValidateIssuer = !string.IsNullOrWhiteSpace(configuration.Issuer),
            ValidAudience = options.Audience,
            ValidateAudience = true,
            IssuerSigningKeys = SigningKeys(),
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero
        });

        if (!result.IsValid ||
            Claim(result, ApplicationTokenClaims.TokenKind) != ApplicationTokenClaims.ApplicationKind ||
            !Guid.TryParse(Claim(result, ApplicationTokenClaims.Id), out var applicationId) ||
            !Guid.TryParse(Claim(result, ApplicationTokenClaims.ScopeId), out var scopeId) ||
            !Guid.TryParse(Claim(result, ApplicationTokenClaims.CredentialId), out var credentialId))
        {
            return null;
        }

        return new ApplicationTokenPrincipal(applicationId, scopeId, credentialId, result.SecurityToken.ValidTo);
    }

    private IEnumerable<SecurityKey> SigningKeys()
    {
        var secrets = configuration.Keys.Count > 0
            ? configuration.Keys.Select(key => key.Secret)
            : [configuration.Secret];

        return secrets.Select(secret => new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secret)));
    }

    private static string? Claim(TokenValidationResult result, string name) =>
        result.Claims.TryGetValue(name, out var value) ? value?.ToString() : null;
}
```

If `JwtConfiguration.Keys` is not an `ICollection`/`IReadOnlyCollection` exposing `Count`, use `configuration.Keys.Any()` instead. Keep everything else as written.

- [ ] **Step 8: Run the service tests to verify they pass**

Run: `dotnet build src/ArturRios.Heimdall.sln && dotnet test tests/Presentation/ArturRios.Heimdall.WebApi.Tests --no-build --filter "FullyQualifiedName~JwtApplicationTokenServiceTests"`
Expected: PASS (14 cases). If `GivenATokenSignedWithThePreviousKey…` fails, check that `ArturRios.Jwt` really reads secrets as ASCII (its `JwtHandler.KeyFrom` remarks say so) before changing anything.

- [ ] **Step 9: Write the failing mapper test**

Add to `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/IdentityUserMapperTests.cs` (add `using ArturRios.Heimdall.Shared.Security;`):

```csharp
    [UnitFact]
    public void GivenATokenKindClaim_WhenReadingClaims_ThenNoIdentityIsBuilt()
    {
        // Given — a claim set that would otherwise be a valid SystemAdmin, but marked as an
        // application token (TH-35): Heimdall's middleware checks no audience, so this is what keeps
        // an application token off every person endpoint even if it ever carried a role
        var claims = Mapper.ToClaims(new IdentityUser(Guid.NewGuid(), (int)Roles.SystemAdmin));
        claims[ApplicationTokenClaims.TokenKind] = ApplicationTokenClaims.ApplicationKind;

        // When
        var user = Mapper.FromClaims(claims);

        // Then
        Assert.Null(user);
    }
```

Run: `dotnet build src/ArturRios.Heimdall.sln && dotnet test tests/Presentation/ArturRios.Heimdall.WebApi.Tests --no-build --filter "FullyQualifiedName~IdentityUserMapperTests"` — Expected: the new test FAILS.

- [ ] **Step 10: Refuse `tokenKind` in the mapper**

In `IdentityUserMapper.FromClaims`, before reading the id, add (and `using ArturRios.Heimdall.Shared.Security;`):

```csharp
        // TH-35: an application token is never a person, whatever else it carries. The middleware
        // validates signature and lifetime only — not the audience — so this is the check that
        // keeps one off every endpoint but UC-57's, which validates application tokens itself.
        if (claims.ContainsKey(ApplicationTokenClaims.TokenKind))
        {
            return null;
        }
```

Add one sentence to the class `<remarks>`: a token carrying `tokenKind` is an application token (UC-56) and yields `null`.

Run the mapper tests again — Expected: PASS.

- [ ] **Step 11: Register the options and the service, and configure every environment**

In `Startup.ConfigureSecurity`, after `Builder.Services.AddSingleton<JwtHandler>();`:

```csharp
        // UC-56/UC-57: application tokens share the signing keys but not the audience. Read here,
        // with the rest of the token configuration, so a missing or equal audience stops start-up
        // before anything is served (TH-35).
        var applicationTokens = ApplicationTokenOptions.FromEnvironment();
        Builder.Services.AddSingleton(applicationTokens);
        Builder.Services.AddScoped<JwtApplicationTokenService>();
        Builder.Services.AddScoped<IApplicationTokenIssuer>(provider =>
            provider.GetRequiredService<JwtApplicationTokenService>());
        Builder.Services.AddScoped<IApplicationTokenValidator>(provider =>
            provider.GetRequiredService<JwtApplicationTokenService>());

        // The audience is not a secret; logging it makes a mismatch with a consumer diagnosable.
        Log.Information(
            "Application tokens: audience {Audience}, lifetime {Seconds}s",
            applicationTokens.Audience, applicationTokens.Lifetime.TotalSeconds);
```

Add `using ArturRios.Heimdall.Shared.Security;` if it is not already there. If the challenge-token service is registered once and exposed through both its interfaces in a different style (`AddDependencies`, around the comment "one class both issues and validates it"), use that style instead.

In `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/Support/PostgresFixture.cs`:
- add `public const string ApplicationTokenAudience = "heimdall-tests-applications";` with a `<summary>` saying it is the audience the host under test gives application tokens, published for the same reason as `PreviousTokenSecret`;
- add `Environment.SetEnvironmentVariable(ApplicationTokenOptions.AudienceVariable, ApplicationTokenAudience);` next to the token audience line (`using ArturRios.Heimdall.WebApi.Security;` is already there).

Environment templates, each beside the `HEIMDALL_AUTH_TOKEN_*` lines:
- `src/Presentation/ArturRios.Heimdall.WebApi/Environments/.env`: `HEIMDALL_APPLICATION_TOKEN_AUDIENCE=[[application-token-audience]]` and `HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS=[[application-token-expiration-in-seconds]]`, placed alphabetically at the top (before `HEIMDALL_AUTH_TOKEN_AUDIENCE`).
- `docker/local.env.example`, `docker/development.env.example`, `docker/homologation.env.example`, `docker/production.env.example`: `HEIMDALL_APPLICATION_TOKEN_AUDIENCE=heimdall-applications` and `HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS=600`, each preceded by a one-line comment saying the audience must differ from `HEIMDALL_AUTH_TOKEN_AUDIENCE`.
- `docker-compose.yml`, after `HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS`:
  ```yaml
      # Must differ from HEIMDALL_AUTH_TOKEN_AUDIENCE, or the API refuses to start (TH-35).
      HEIMDALL_APPLICATION_TOKEN_AUDIENCE: ${HEIMDALL_APPLICATION_TOKEN_AUDIENCE:-heimdall-applications}
      HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS: ${HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS:-600}
  ```

- [ ] **Step 12: Run the Web API suite**

Run: `dotnet build src/ArturRios.Heimdall.sln && dotnet test tests/Presentation/ArturRios.Heimdall.WebApi.Tests --no-build`
Expected: PASS. Every functional host now starts with the new required variable; a failure naming `HEIMDALL_APPLICATION_TOKEN_AUDIENCE` means the fixture line is missing or runs too late.

- [ ] **Step 13: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Shared/Security/ApplicationTokenClaims.cs \
  src/Application/ArturRios.Heimdall.Shared/Security/IApplicationTokenValidator.cs \
  src/Application/ArturRios.Heimdall.Command/Services/IApplicationTokenIssuer.cs \
  src/Presentation/ArturRios.Heimdall.WebApi/Security/ApplicationTokenOptions.cs \
  src/Presentation/ArturRios.Heimdall.WebApi/Security/JwtApplicationTokenService.cs \
  src/Presentation/ArturRios.Heimdall.WebApi/Security/IdentityUserMapper.cs \
  src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs \
  src/Presentation/ArturRios.Heimdall.WebApi/Environments/.env \
  docker/local.env.example docker/development.env.example \
  docker/homologation.env.example docker/production.env.example docker-compose.yml \
  tests/Presentation/ArturRios.Heimdall.WebApi.Tests/Support/PostgresFixture.cs \
  tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationTokenOptionsTests.cs \
  tests/Presentation/ArturRios.Heimdall.WebApi.Tests/JwtApplicationTokenServiceTests.cs \
  tests/Presentation/ArturRios.Heimdall.WebApi.Tests/IdentityUserMapperTests.cs
git commit -m "feat: sign and validate application tokens" -m "..."
```

Body: the separate required audience, the ten-minute default, the `tokenKind` refusal in the mapper and why (the middleware checks no audience); then the two trailer lines.

---

### Task 3: `POST …/applications/{id}/credentials` — issue a credential (UC-53)

**Files:**
- Move: `src/Application/ArturRios.Heimdall.Query/Input/UtcInstant.cs` → `src/Application/ArturRios.Heimdall.Shared/Time/UtcInstant.cs`; `tests/Application/ArturRios.Heimdall.Query.Tests/UtcInstantTests.cs` → `tests/Application/ArturRios.Heimdall.Shared.Tests/UtcInstantTests.cs`
- Modify: `src/Application/ArturRios.Heimdall.Query/Handlers/ListAuditLogQueryHandler.cs`, `src/Application/ArturRios.Heimdall.Query/Input/Validation/ListAuditLogQueryValidator.cs` (namespace only)
- Create: `src/Application/ArturRios.Heimdall.Shared/Services/ApplicationManagementAccess.cs`
- Create: `src/Application/ArturRios.Heimdall.Shared/Messages/ApplicationCredentialMessages.cs`, `ApplicationCredentialMessageMap.cs`
- Create: `src/Domain/ArturRios.Heimdall.Domain/Persistence/IApplicationCredentialSlots.cs`, `src/Infrastructure/ArturRios.Heimdall.Data/Persistence/ApplicationCredentialSlots.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Input/IssueApplicationCredentialCommand.cs`, `Input/Validation/IssueApplicationCredentialCommandValidator.cs`, `Output/IssueApplicationCredentialCommandOutput.cs`, `Handlers/IssueApplicationCredentialCommandHandler.cs`
- Create: `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/ApplicationCredentialController.cs`
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`
- Test: `tests/Application/ArturRios.Heimdall.Shared.Tests/ApplicationManagementAccessTests.cs`, `tests/Application/ArturRios.Heimdall.Command.Tests/Support/InMemoryApplicationCredentialSlots.cs`, `tests/Application/ArturRios.Heimdall.Command.Tests/IssueApplicationCredentialCommandValidatorTests.cs`, `tests/Application/ArturRios.Heimdall.Command.Tests/IssueApplicationCredentialCommandHandlerTests.cs`, `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/Support/ApplicationSeeds.cs`, `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationCredentialControllerIssueTests.cs`

**Interfaces:**
- Consumes: Task 1's `ApplicationCredential` (`LiveAt`, `MaxLive`), `ApplicationCredentialSecret`; `ApplicationMessages.ApplicationNotFound`; `IActorScoped`; `HttpContext.ApplyActor`.
- Produces:
  - `UtcInstant.Of(DateTime)` in namespace `ArturRios.Heimdall.Shared.Time`.
  - `enum ApplicationAccess { Granted, NotFound, Forbidden }`, `record ApplicationAccessResult(ApplicationAccess Access, Application? Application)`, `ApplicationManagementAccess.ResolveAsync(IQueryable<Application> applications, Guid scopeId, Guid applicationId, int actingRole, Guid actingPersonId) → Task<ApplicationAccessResult>` (namespace `ArturRios.Heimdall.Shared.Services`). `Application` is non-null only when `Access == Granted`.
  - `ApplicationCredentialMessages` (all eight texts of Decision 3 — declare them all now; Task 4 maps the remaining three) and `ApplicationCredentialMessageMap.StatusCodes`.
  - `IApplicationCredentialSlots.TryAddWithinLimitAsync(ApplicationCredential credential, int maxLive, DateTime now) → Task<bool>`.
  - `ApplicationCredentialController` with route `api/scopes/{scopeId:guid}/applications/{id:guid}/credentials` and action `Issue`.
  - `ApplicationSeeds` (test support): `SeedScopeAsync`, `SeedScopeAdminAsync`, `SeedApplicationAsync`, `SeedCredentialAsync`, `CredentialsOfAsync`, `Route` — signatures in Step 9.

- [ ] **Step 1: Move `UtcInstant` to Shared**

```bash
mkdir -p src/Application/ArturRios.Heimdall.Shared/Time
git mv src/Application/ArturRios.Heimdall.Query/Input/UtcInstant.cs src/Application/ArturRios.Heimdall.Shared/Time/UtcInstant.cs
git mv tests/Application/ArturRios.Heimdall.Query.Tests/UtcInstantTests.cs tests/Application/ArturRios.Heimdall.Shared.Tests/UtcInstantTests.cs
```

In the moved source, set `namespace ArturRios.Heimdall.Shared.Time;` and widen the summary's first sentence to "Reads an instant a caller sent as the UTC instant this API's contract says it is (UC-48's `From`/`To`, UC-53's `ExpiresAt`)." In the moved test, set `namespace ArturRios.Heimdall.Shared.Tests;` and `using ArturRios.Heimdall.Shared.Time;`. In `ListAuditLogQueryHandler.cs` and `ListAuditLogQueryValidator.cs`, add `using ArturRios.Heimdall.Shared.Time;` (the old type lived in their own `Query.Input` namespace, so nothing else changes).

Run: `dotnet build src/ArturRios.Heimdall.sln && dotnet test tests/Application/ArturRios.Heimdall.Shared.Tests --no-build && dotnet test tests/Application/ArturRios.Heimdall.Query.Tests --no-build` (one after the other). Expected: PASS, with the `UtcInstantTests` count now under Shared.

- [ ] **Step 2: Write the failing access-rule tests**

Create `tests/Application/ArturRios.Heimdall.Shared.Tests/ApplicationManagementAccessTests.cs`:

```csharp
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Shared.Tests;

// Unit tests for ApplicationManagementAccess (UC-53…UC-55): the rule UC-17/UC-18 already apply to an
// application — a System Admin manages any; a Scope Admin only one they own and whose scope they
// still own — with "not found" (missing, in another scope, logically deleted, or its scope logically
// deleted) answered before authorization so both outcomes stay observable.
public class ApplicationManagementAccessTests
{
    private static async Task<(AsyncFakeRepository<Application, long> Applications, Application Application, Person Owner, Scope Scope)>
        SeedAsync(bool applicationDeleted = false, bool scopeDeleted = false, bool ownerStillOwnsScope = true)
    {
        var scope = new Scope { Id = 1, PublicId = Guid.NewGuid(), Name = "Acme", IsDeleted = scopeDeleted };
        var owner = new Person
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            Name = "Owner",
            Email = "owner@test.local",
            RoleId = (long)Roles.ScopeAdmin,
            ScopeOwnerships = ownerStillOwnsScope ? [new ScopeOwner { ScopeId = scope.Id, Scope = scope }] : []
        };
        var application = new Application
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            Name = "Billing Service",
            IsDeleted = applicationDeleted,
            ScopeId = scope.Id,
            Scope = scope,
            OwnerId = owner.Id,
            Owner = owner
        };
        var applications = new AsyncFakeRepository<Application, long>();
        await applications.CreateAsync(application);

        return (applications, application, owner, scope);
    }

    [UnitFact]
    public async Task GivenASystemAdmin_WhenResolving_ThenAccessIsGranted()
    {
        // Given
        var (applications, application, _, scope) = await SeedAsync();

        // When
        var result = await ApplicationManagementAccess.ResolveAsync(
            applications.Query(), scope.PublicId, application.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid());

        // Then
        Assert.Equal(ApplicationAccess.Granted, result.Access);
        Assert.Same(application, result.Application);
    }

    [UnitFact]
    public async Task GivenTheOwningScopeAdmin_WhenResolving_ThenAccessIsGranted()
    {
        // Given
        var (applications, application, owner, scope) = await SeedAsync();

        // When
        var result = await ApplicationManagementAccess.ResolveAsync(
            applications.Query(), scope.PublicId, application.PublicId, (int)Roles.ScopeAdmin, owner.PublicId);

        // Then
        Assert.Equal(ApplicationAccess.Granted, result.Access);
    }

    [UnitFact]
    public async Task GivenAnotherScopeAdmin_WhenResolving_ThenAccessIsForbidden()
    {
        // Given — owning the scope is not owning the application
        var (applications, application, _, scope) = await SeedAsync();

        // When
        var result = await ApplicationManagementAccess.ResolveAsync(
            applications.Query(), scope.PublicId, application.PublicId, (int)Roles.ScopeAdmin, Guid.NewGuid());

        // Then
        Assert.Equal(ApplicationAccess.Forbidden, result.Access);
        Assert.Null(result.Application);
    }

    [UnitFact]
    public async Task GivenAnOwnerRemovedFromTheScope_WhenResolving_ThenAccessIsForbidden()
    {
        // Given — UC-22 took the scope away but left them named as owner (AF-17b)
        var (applications, application, owner, scope) = await SeedAsync(ownerStillOwnsScope: false);

        // When
        var result = await ApplicationManagementAccess.ResolveAsync(
            applications.Query(), scope.PublicId, application.PublicId, (int)Roles.ScopeAdmin, owner.PublicId);

        // Then
        Assert.Equal(ApplicationAccess.Forbidden, result.Access);
    }

    [UnitTheory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task GivenNoLiveApplicationInTheScope_WhenResolving_ThenItIsNotFound(
        bool applicationDeleted, bool scopeDeleted, bool otherScope)
    {
        // Given — deleted application, deleted scope, or the route naming another scope
        var (applications, application, _, scope) = await SeedAsync(applicationDeleted, scopeDeleted);
        var routeScope = otherScope ? Guid.NewGuid() : scope.PublicId;

        // When — even a System Admin
        var result = await ApplicationManagementAccess.ResolveAsync(
            applications.Query(), routeScope, application.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid());

        // Then
        Assert.Equal(ApplicationAccess.NotFound, result.Access);
        Assert.Null(result.Application);
    }
}
```

Run: `dotnet build src/ArturRios.Heimdall.sln` — Expected: build FAILS, `ApplicationManagementAccess` not found.

- [ ] **Step 3: Write the access rule**

Create `src/Application/ArturRios.Heimdall.Shared/Services/ApplicationManagementAccess.cs`:

```csharp
using ArturRios.Heimdall.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Shared.Services;

/// <summary>The outcome of <see cref="ApplicationManagementAccess.ResolveAsync" />.</summary>
public enum ApplicationAccess
{
    /// <summary>The actor may manage the application.</summary>
    Granted,

    /// <summary>No live application with that id lives in the addressed, live scope.</summary>
    NotFound,

    /// <summary>The application exists, but the actor may not manage it.</summary>
    Forbidden
}

/// <summary>The resolved application — only when <see cref="Access" /> is <see cref="ApplicationAccess.Granted" />.</summary>
public sealed record ApplicationAccessResult(ApplicationAccess Access, Application? Application);

/// <summary>
///     Who may manage an application's credentials (UC-53…UC-55, FR-AP-12): the rule UC-17, UC-18
///     and UC-19 already apply to the application itself.
/// </summary>
/// <remarks>
///     <para>
///         A System Admin manages any application. A Scope Admin manages one only when they are its
///         owner <em>and</em> still own its scope: owning the scope is not grounds to manage another
///         owner's application, and an owner UC-22 removed from the scope keeps nothing (AF-17b).
///     </para>
///     <para>
///         Not found is answered before authorization, as UC-17 does, so a missing application and
///         one the actor may not touch stay distinguishable to the people allowed to see either. A
///         logically deleted application, or one whose scope is logically deleted, is not found:
///         its credentials are inert (UC-56, UC-57), so there is nothing to manage.
///     </para>
/// </remarks>
public static class ApplicationManagementAccess
{
    public static async Task<ApplicationAccessResult> ResolveAsync(
        IQueryable<Application> applications,
        Guid scopeId,
        Guid applicationId,
        int actingRole,
        Guid actingPersonId)
    {
        var application = await applications
            .Include(x => x.Scope)
            .Include(x => x.Owner)
            .ThenInclude(owner => owner.ScopeOwnerships)
            .FirstOrDefaultAsync(x => x.PublicId == applicationId && x.Scope.PublicId == scopeId &&
                                      !x.IsDeleted && !x.Scope.IsDeleted);

        if (application is null)
        {
            return new ApplicationAccessResult(ApplicationAccess.NotFound, null);
        }

        if (actingRole == (int)Roles.SystemAdmin)
        {
            return new ApplicationAccessResult(ApplicationAccess.Granted, application);
        }

        var ownsApplicationAndScope =
            application.Owner.PublicId == actingPersonId &&
            application.Owner.ScopeOwnerships.Any(ownership => ownership.ScopeId == application.ScopeId);

        return ownsApplicationAndScope
            ? new ApplicationAccessResult(ApplicationAccess.Granted, application)
            : new ApplicationAccessResult(ApplicationAccess.Forbidden, null);
    }
}
```

Run: `dotnet build src/ArturRios.Heimdall.sln && dotnet test tests/Application/ArturRios.Heimdall.Shared.Tests --no-build --filter "FullyQualifiedName~ApplicationManagementAccessTests"` — Expected: PASS (7 cases).

- [ ] **Step 4: Write the messages and the map**

Create `src/Application/ArturRios.Heimdall.Shared/Messages/ApplicationCredentialMessages.cs`, one `public const string` per row of Decision 3's first table, each with a one-line `<summary>` naming its UC/AF:

```csharp
namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>Messages of UC-53 (issue), UC-54 (list) and UC-55 (revoke) — an application's credentials.</summary>
public static class ApplicationCredentialMessages
{
    /// <summary>UC-53 main flow.</summary>
    public const string CredentialIssued =
        "Application credential issued. Store the secret now: it is never shown again.";

    /// <summary>UC-54 main flow.</summary>
    public const string CredentialsRetrieved = "Application credentials retrieved.";

    /// <summary>UC-55 main flow.</summary>
    public const string CredentialRevoked = "Application credential revoked.";

    /// <summary>AF-53c — the requested expiry is not in the future.</summary>
    public const string ExpiryNotInFuture = "The expiry must be in the future.";

    /// <summary>AF-53b, AF-54b, AF-55b — the actor may not manage this application.</summary>
    public const string NotAuthorizedToManageCredentials =
        "You are not allowed to manage this application's credentials.";

    /// <summary>AF-55c — no such credential on this application.</summary>
    public const string CredentialNotFound = "Application credential not found.";

    /// <summary>AF-53d — two live credentials already exist.</summary>
    public const string CredentialLimitReached =
        "This application already has two live credentials. Revoke one before issuing another.";

    /// <summary>AF-55d — the credential was already revoked.</summary>
    public const string CredentialAlreadyRevoked = "This application credential is already revoked.";
}
```

Create `src/Application/ArturRios.Heimdall.Shared/Messages/ApplicationCredentialMessageMap.cs` (Task 4 adds the UC-54/UC-55 rows):

```csharp
using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>Status codes for <c>ApplicationCredentialController</c>'s messages (UC-53…UC-55).</summary>
public static class ApplicationCredentialMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes =
        DataAccessMessageMap.CombinedWith(new Dictionary<string, int>
        {
            // UC-53 main flow — credential issued.
            [ApplicationCredentialMessages.CredentialIssued] = HttpStatusCodes.Created,
            // AF-53a — no live application in the addressed, live scope.
            [ApplicationMessages.ApplicationNotFound] = HttpStatusCodes.NotFound,
            // AF-53b — the actor may not manage the application.
            [ApplicationCredentialMessages.NotAuthorizedToManageCredentials] = HttpStatusCodes.Forbidden,
            // AF-53c — the expiry is not in the future.
            [ApplicationCredentialMessages.ExpiryNotInFuture] = HttpStatusCodes.BadRequest,
            // AF-53d — two live credentials already.
            [ApplicationCredentialMessages.CredentialLimitReached] = HttpStatusCodes.Conflict
        });
}
```

If `Shared.Tests` has a test asserting every `*Messages` constant appears in some map (see `AuditMessageMapTests`/`DataAccessMessageMapTests`), add an `ApplicationCredentialMessageMapTests` in its style asserting the five rows above; Task 4 extends it.

- [ ] **Step 5: Write the slot interface, the in-memory fake and the database implementation**

Create `src/Domain/ArturRios.Heimdall.Domain/Persistence/IApplicationCredentialSlots.cs`:

```csharp
using ArturRios.Heimdall.Domain.Entities;

namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>
///     Adds a credential only while its application holds fewer than the allowed number of live
///     ones (UC-53 AF-53d, FR-AP-11), as one step that concurrent requests cannot both pass.
/// </summary>
public interface IApplicationCredentialSlots
{
    /// <param name="credential">The new credential, its <c>ApplicationId</c> set.</param>
    /// <param name="maxLive">How many live credentials the application may hold.</param>
    /// <param name="now">The instant liveness is judged at.</param>
    /// <returns><see langword="true" /> when the credential was stored; <see langword="false" /> when the limit was already reached.</returns>
    Task<bool> TryAddWithinLimitAsync(ApplicationCredential credential, int maxLive, DateTime now);
}
```

Create `tests/Application/ArturRios.Heimdall.Command.Tests/Support/InMemoryApplicationCredentialSlots.cs`:

```csharp
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>
///     <see cref="IApplicationCredentialSlots" /> over an <see cref="AsyncFakeRepository{TEntity,TKey}" />:
///     the same count-then-add, without the lock a single-threaded test does not need.
/// </summary>
public sealed class InMemoryApplicationCredentialSlots(AsyncFakeRepository<ApplicationCredential, long> credentials)
    : IApplicationCredentialSlots
{
    public async Task<bool> TryAddWithinLimitAsync(ApplicationCredential credential, int maxLive, DateTime now)
    {
        var live = credentials.Query()
            .Where(x => x.ApplicationId == credential.ApplicationId)
            .Count(ApplicationCredential.LiveAt(now));

        if (live >= maxLive)
        {
            return false;
        }

        await credentials.CreateAsync(credential);

        return true;
    }
}
```

Create `src/Infrastructure/ArturRios.Heimdall.Data/Persistence/ApplicationCredentialSlots.cs`:

```csharp
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Data.Persistence;

/// <summary>
///     <see cref="IApplicationCredentialSlots" /> on PostgreSQL: a transaction that locks the
///     application's row, counts its live credentials, and inserts only below the limit.
/// </summary>
/// <remarks>
///     A count followed by an insert is a race on its own — two requests both count one, both
///     insert, and the application holds three live secrets. <c>SELECT … FOR UPDATE</c> on the
///     application's row makes a second issuer for the same application wait until the first commits,
///     and then count the row the first inserted. Other applications are not blocked. Unlike
///     <see cref="AtomicWrites" />, this needs a transaction, because the rule spans rows a single
///     conditional <c>UPDATE</c> cannot see.
/// </remarks>
public sealed class ApplicationCredentialSlots(AppDbContext context) : IApplicationCredentialSlots
{
    public async Task<bool> TryAddWithinLimitAsync(ApplicationCredential credential, int maxLive, DateTime now)
    {
        // The table name comes from the model, never from input, so building it into the SQL is safe;
        // the id stays a parameter.
        var applicationTable = context.Model.FindEntityType(typeof(Application))!.GetSchemaQualifiedTableName();
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();

            await context.Database.ExecuteSqlRawAsync(
                $"SELECT 1 FROM {applicationTable} WHERE id = {{0}} FOR UPDATE", credential.ApplicationId);

            var live = await context.ApplicationCredentials
                .Where(x => x.ApplicationId == credential.ApplicationId)
                .CountAsync(ApplicationCredential.LiveAt(now));

            if (live >= maxLive)
            {
                await transaction.RollbackAsync();

                return false;
            }

            context.ApplicationCredentials.Add(credential);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            return true;
        });
    }
}
```

Register it in `Startup.AddDependencies`, right after `Builder.Services.AddScoped<IAtomicWrites, AtomicWrites>();`:

```csharp
        // UC-53's two-live-credentials limit, held under a row lock so concurrent issues cannot pass it.
        Builder.Services.AddScoped<IApplicationCredentialSlots, ApplicationCredentialSlots>();
```

- [ ] **Step 6: Write the failing validator and handler tests**

Create `tests/Application/ArturRios.Heimdall.Command.Tests/IssueApplicationCredentialCommandValidatorTests.cs`:

```csharp
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for IssueApplicationCredentialCommandValidator (UC-53 AF-53c): no expiry is valid, a
// future one is valid, and one at or before now is refused — read as UTC when it carries no offset.
public class IssueApplicationCredentialCommandValidatorTests
{
    private static readonly IssueApplicationCredentialCommandValidator Validator = new();

    [UnitFact]
    public async Task GivenNoExpiry_WhenValidating_ThenItIsValid()
    {
        // Given
        var command = new IssueApplicationCredentialCommand();

        // When
        var result = await Validator.ValidateAsync(command);

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenAFutureExpiry_WhenValidating_ThenItIsValid()
    {
        // Given
        var command = new IssueApplicationCredentialCommand { ExpiresAt = DateTime.UtcNow.AddDays(30) };

        // When
        var result = await Validator.ValidateAsync(command);

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenAPastExpiry_WhenValidating_ThenItIsRefused()
    {
        // Given
        var command = new IssueApplicationCredentialCommand { ExpiresAt = DateTime.UtcNow.AddMinutes(-1) };

        // When
        var result = await Validator.ValidateAsync(command);

        // Then
        Assert.Equal([ApplicationCredentialMessages.ExpiryNotInFuture], result.Errors.Select(e => e.ErrorMessage));
    }

    [UnitFact]
    public async Task GivenAnExpiryWithoutAnOffset_WhenValidating_ThenItIsReadAsUtc()
    {
        // Given — one hour ago in UTC, with no kind; read as local time on a machine east of UTC it
        // would be in the future
        var past = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-1), DateTimeKind.Unspecified);
        var command = new IssueApplicationCredentialCommand { ExpiresAt = past };

        // When
        var result = await Validator.ValidateAsync(command);

        // Then
        Assert.False(result.IsValid);
    }
}
```

Create `tests/Application/ArturRios.Heimdall.Command.Tests/IssueApplicationCredentialCommandHandlerTests.cs`. Follow `UpdateApplicationCommandHandlerTests` for structure (local seed helpers, a `Handler(...)` factory, a `ValidValidator()` mock):

```csharp
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for IssueApplicationCredentialCommandHandler (UC-53): the main flow for a System Admin
// and for the owning Scope Admin (a secret returned once, only its hash stored), AF-53a (no live
// application in the scope), AF-53b (a Scope Admin who does not own it), AF-53c (validation) and
// AF-53d (two live credentials already — expired and revoked ones do not count).
public class IssueApplicationCredentialCommandHandlerTests
{
    private readonly AsyncFakeRepository<Application, long> _applications = new();
    private readonly AsyncFakeRepository<ApplicationCredential, long> _credentials = new();

    private static Mock<IValidator<IssueApplicationCredentialCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<IssueApplicationCredentialCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<IssueApplicationCredentialCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private IssueApplicationCredentialCommandHandler Handler(Mock<IValidator<IssueApplicationCredentialCommand>>? validator = null) =>
        new((validator ?? ValidValidator()).Object, _applications, new InMemoryApplicationCredentialSlots(_credentials));

    private async Task<(Application Application, Person Owner)> SeedApplicationAsync(bool isDeleted = false)
    {
        var scope = new Scope { Id = 1, PublicId = Guid.NewGuid(), Name = "Acme" };
        var owner = new Person
        {
            Id = 1, PublicId = Guid.NewGuid(), Name = "Owner", Email = "owner@test.local",
            RoleId = (long)Roles.ScopeAdmin,
            ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id, Scope = scope }]
        };
        var application = new Application
        {
            Id = 10, PublicId = Guid.NewGuid(), Name = "Billing Service", IsDeleted = isDeleted,
            ScopeId = scope.Id, Scope = scope, OwnerId = owner.Id, Owner = owner
        };
        await _applications.CreateAsync(application);
        return (application, owner);
    }

    private static IssueApplicationCredentialCommand Command(
        Application application, int role, Guid actor, DateTime? expiresAt = null) => new()
    {
        ScopeId = application.Scope.PublicId,
        Id = application.PublicId,
        ExpiresAt = expiresAt,
        ActingRole = role,
        ActingPersonId = actor
    };

    [UnitFact]
    public async Task GivenASystemAdmin_WhenIssuing_ThenASecretIsReturnedAndOnlyItsHashIsStored()
    {
        // Given
        var (application, _) = await SeedApplicationAsync();

        // When
        var output = await Handler().HandleAsync(Command(application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(ApplicationCredentialMessages.CredentialIssued, output.Messages.Single());
        var secret = output.Data!.Secret;
        Assert.StartsWith(ApplicationCredentialSecret.Prefix, secret);
        var stored = Assert.Single(_credentials.Query());
        Assert.Equal(application.Id, stored.ApplicationId);
        Assert.Equal(ApplicationCredentialSecret.HashOf(secret), stored.SecretHash);
        Assert.DoesNotContain(secret, stored.SecretHash);
        Assert.Equal(stored.PublicId, output.Data.Id);
        Assert.Null(stored.ExpiresAt);
        Assert.Null(output.Data.ExpiresAt);
    }

    [UnitFact]
    public async Task GivenTheOwningScopeAdminAndAnExpiry_WhenIssuing_ThenTheExpiryIsStoredAsUtc()
    {
        // Given
        var (application, owner) = await SeedApplicationAsync();
        var expiresAt = DateTime.SpecifyKind(new DateTime(2030, 1, 1, 0, 0, 0), DateTimeKind.Unspecified);

        // When
        var output = await Handler().HandleAsync(Command(application, (int)Roles.ScopeAdmin, owner.PublicId, expiresAt));

        // Then
        Assert.True(output.Success);
        var stored = Assert.Single(_credentials.Query());
        Assert.Equal(DateTimeKind.Utc, stored.ExpiresAt!.Value.Kind);
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), stored.ExpiresAt);
        Assert.Equal(stored.ExpiresAt, output.Data!.ExpiresAt);
    }

    [UnitFact]
    public async Task GivenNoLiveApplication_WhenIssuing_ThenReturnsApplicationNotFound()
    {
        // Given
        var (application, _) = await SeedApplicationAsync(isDeleted: true);

        // When
        var output = await Handler().HandleAsync(Command(application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Equal(ApplicationMessages.ApplicationNotFound, output.Errors.Single());
        Assert.Empty(_credentials.Query());
    }

    [UnitFact]
    public async Task GivenAScopeAdminWhoDoesNotOwnIt_WhenIssuing_ThenReturnsNotAuthorized()
    {
        // Given
        var (application, _) = await SeedApplicationAsync();

        // When
        var output = await Handler().HandleAsync(Command(application, (int)Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Equal(ApplicationCredentialMessages.NotAuthorizedToManageCredentials, output.Errors.Single());
        Assert.Empty(_credentials.Query());
    }

    [UnitFact]
    public async Task GivenAnInvalidCommand_WhenIssuing_ThenTheValidatorsMessageIsReturned()
    {
        // Given
        var (application, _) = await SeedApplicationAsync();
        var validator = new Mock<IValidator<IssueApplicationCredentialCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<IssueApplicationCredentialCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("ExpiresAt", ApplicationCredentialMessages.ExpiryNotInFuture)]));

        // When
        var output = await Handler(validator).HandleAsync(Command(application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.Equal(ApplicationCredentialMessages.ExpiryNotInFuture, output.Errors.Single());
        Assert.Empty(_credentials.Query());
    }

    [UnitFact]
    public async Task GivenTwoLiveCredentials_WhenIssuing_ThenReturnsLimitReached()
    {
        // Given
        var (application, _) = await SeedApplicationAsync();
        await _credentials.CreateAsync(new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a" });
        await _credentials.CreateAsync(new ApplicationCredential { Id = 2, ApplicationId = application.Id, SecretHash = "b" });

        // When
        var output = await Handler().HandleAsync(Command(application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.Equal(ApplicationCredentialMessages.CredentialLimitReached, output.Errors.Single());
        Assert.Equal(2, _credentials.Query().Count());
    }

    [UnitFact]
    public async Task GivenOneLiveOneRevokedAndOneExpired_WhenIssuing_ThenItSucceeds()
    {
        // Given — only live credentials count against the limit
        var (application, _) = await SeedApplicationAsync();
        await _credentials.CreateAsync(new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a" });
        await _credentials.CreateAsync(new ApplicationCredential
        {
            Id = 2, ApplicationId = application.Id, SecretHash = "b", RevokedAt = DateTime.UtcNow.AddDays(-1)
        });
        await _credentials.CreateAsync(new ApplicationCredential
        {
            Id = 3, ApplicationId = application.Id, SecretHash = "c", ExpiresAt = DateTime.UtcNow.AddDays(-1)
        });

        // When
        var output = await Handler().HandleAsync(Command(application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(4, _credentials.Query().Count());
    }
}
```

`AsyncFakeRepository` assigns ids for `long` keys when `Id` is 0, so the explicit ids above only keep the seeded rows distinct. If `output.Messages` is named differently on `DataOutput` (check an existing handler test's assertion on the success message), use that name.

Run: `dotnet build src/ArturRios.Heimdall.sln` — Expected: build FAILS on the missing command, validator and handler.

- [ ] **Step 7: Write the command, validator, output and handler**

`src/Application/ArturRios.Heimdall.Command/Input/IssueApplicationCredentialCommand.cs`:

```csharp
using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Issues a secret an application can exchange for an application token (UC-53, FR-AP-10).
///     The route supplies the scope and the application; the body only the optional expiry.
/// </summary>
public class IssueApplicationCredentialCommand : BaseCommand, IActorScoped
{
    /// <summary>The scope's <c>PublicId</c>, from the route.</summary>
    [JsonIgnore]
    public Guid ScopeId { get; set; }

    /// <summary>The application's <c>PublicId</c>, from the route.</summary>
    [JsonIgnore]
    public Guid Id { get; set; }

    /// <summary>When the credential stops being accepted; omitted for never. Must be in the future.</summary>
    public DateTime? ExpiresAt { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
```

`src/Application/ArturRios.Heimdall.Command/Input/Validation/IssueApplicationCredentialCommandValidator.cs`:

```csharp
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Time;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>UC-53 AF-53c: an expiry, when given, must lie in the future — read as UTC when it has no offset.</summary>
public class IssueApplicationCredentialCommandValidator : AbstractValidator<IssueApplicationCredentialCommand>
{
    public IssueApplicationCredentialCommandValidator()
    {
        RuleFor(command => command.ExpiresAt)
            .Must(expiresAt => expiresAt is null || UtcInstant.Of(expiresAt.Value) > DateTime.UtcNow)
            .WithMessage(ApplicationCredentialMessages.ExpiryNotInFuture);
    }
}
```

`src/Application/ArturRios.Heimdall.Command/Output/IssueApplicationCredentialCommandOutput.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     UC-53's answer. <see cref="Secret" /> appears here and nowhere else, ever; <see cref="Id" />
///     is the credential's <c>PublicId</c> and the audit target.
/// </summary>
public class IssueApplicationCredentialCommandOutput : CommandOutput
{
    public Guid Id { get; set; }

    public string Secret { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }
}
```

`src/Application/ArturRios.Heimdall.Command/Handlers/IssueApplicationCredentialCommandHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Heimdall.Shared.Time;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="IssueApplicationCredentialCommand" /> (UC-53, FR-AP-10/11/12): generates a
///     secret, stores its hash, and returns the secret once.
/// </summary>
/// <remarks>
///     The limit of <see cref="ApplicationCredential.MaxLive" /> is checked and the row written by
///     <see cref="IApplicationCredentialSlots" /> in one locked step; checking here and inserting
///     through a repository would let two concurrent requests both pass.
/// </remarks>
public class IssueApplicationCredentialCommandHandler(
    IValidator<IssueApplicationCredentialCommand> validator,
    IAsyncReadOnlyRepository<Application, long> applicationReader,
    IApplicationCredentialSlots slots)
    : ICommandHandlerAsync<IssueApplicationCredentialCommand, IssueApplicationCredentialCommandOutput>
{
    public async Task<DataOutput<IssueApplicationCredentialCommandOutput?>> HandleAsync(
        IssueApplicationCredentialCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<IssueApplicationCredentialCommandOutput?>.New;

        // AF-53c.
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-53a, then AF-53b.
        var access = await ApplicationManagementAccess.ResolveAsync(
            applicationReader.Query(), command.ScopeId, command.Id, command.ActingRole, command.ActingPersonId);

        switch (access.Access)
        {
            case ApplicationAccess.NotFound:
                return output.WithError(ApplicationMessages.ApplicationNotFound);
            case ApplicationAccess.Forbidden:
                return output.WithError(ApplicationCredentialMessages.NotAuthorizedToManageCredentials);
        }

        var now = DateTime.UtcNow;
        var secret = ApplicationCredentialSecret.New();
        var credential = new ApplicationCredential
        {
            ApplicationId = access.Application!.Id,
            SecretHash = ApplicationCredentialSecret.HashOf(secret),
            CreatedAt = now,
            ExpiresAt = command.ExpiresAt is { } expiresAt ? UtcInstant.Of(expiresAt) : null
        };

        // AF-53d.
        if (!await slots.TryAddWithinLimitAsync(credential, ApplicationCredential.MaxLive, now))
        {
            return output.WithError(ApplicationCredentialMessages.CredentialLimitReached);
        }

        return output
            .WithData(new IssueApplicationCredentialCommandOutput
            {
                Id = credential.PublicId,
                Secret = secret,
                CreatedAt = credential.CreatedAt,
                ExpiresAt = credential.ExpiresAt
            })
            .WithMessage(ApplicationCredentialMessages.CredentialIssued);
    }
}
```

Run: `dotnet build src/ArturRios.Heimdall.sln && dotnet test tests/Application/ArturRios.Heimdall.Command.Tests --no-build --filter "FullyQualifiedName~IssueApplicationCredential"` — Expected: PASS (11 tests).

- [ ] **Step 8: Register the handler and write the controller**

In `Startup.AddDependencies`, after the application handlers (`HardDeleteApplicationCommand` line):

```csharp
        Builder.Services.AddScoped<IValidator<IssueApplicationCredentialCommand>, IssueApplicationCredentialCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<IssueApplicationCredentialCommand, IssueApplicationCredentialCommandOutput, IssueApplicationCredentialCommandHandler>();
```

Create `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/ApplicationCredentialController.cs`:

```csharp
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ArturRios.Heimdall.WebApi.Controllers;

/// <summary>
///     An application's credentials (UC-53…UC-55, FR-AP-10…FR-AP-12): the secrets it exchanges for an
///     application token at <c>POST /api/applications/token</c>.
/// </summary>
/// <remarks>
///     A <c>User</c> can own no application (FR-AP-03) and is refused by the attribute. Among System
///     Admins and Scope Admins the rule is data-dependent and lives in the handlers, through
///     <c>ApplicationManagementAccess</c> — the same rule as reading or renaming the application.
/// </remarks>
[Route("api/scopes/{scopeId:guid}/applications/{id:guid}/credentials")]
public class ApplicationCredentialController(CommandMediator commandMediator, QueryMediator queryMediator) : Controller
{
    /// <summary>
    ///     Issues a credential (UC-53). The body is optional and carries only <c>expiresAt</c>. The
    ///     answer is the only place the secret ever appears; Heimdall keeps its hash alone.
    /// </summary>
    [HttpPost]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<IssueApplicationCredentialCommandOutput?>>> Issue(
        Guid scopeId, Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] IssueApplicationCredentialCommand? command)
    {
        command ??= new IssueApplicationCredentialCommand();
        command.ScopeId = scopeId;
        command.Id = id;
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<IssueApplicationCredentialCommand, IssueApplicationCredentialCommandOutput>(command);

        return result.ToActionResult(statusMap: ApplicationCredentialMessageMap.StatusCodes);
    }
}
```

`queryMediator` is unused until Task 4; if the build treats an unused primary-constructor parameter as an error, add the parameter in Task 4 instead.

- [ ] **Step 9: Write the shared functional seeds**

Create `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/Support/ApplicationSeeds.cs`:

```csharp
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Seeds the scopes, owners, applications and credentials UC-53…UC-57's functional tests share,
///     straight through the database, so a test's Given never depends on the endpoint it tests.
/// </summary>
public static class ApplicationSeeds
{
    public static string Route(Guid scopeId, Guid applicationId) =>
        $"/api/scopes/{scopeId}/applications/{applicationId}/credentials";

    public static async Task<Scope> SeedScopeAsync(PostgresFixture db)
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    public static async Task<Person> SeedScopeAdminAsync(PostgresFixture db, Scope? ownedScope = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin, EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        if (ownedScope is not null)
        {
            context.ScopeOwners.Add(new ScopeOwner { ScopeId = ownedScope.Id, PersonId = person.Id });
            await context.SaveChangesAsync();
        }

        return person;
    }

    public static async Task<Application> SeedApplicationAsync(PostgresFixture db, Scope scope, Person owner)
    {
        await using var context = db.CreateContext();
        var application = new Application
        {
            PublicId = Guid.NewGuid(), Name = $"app-{Guid.NewGuid():N}",
            ScopeId = scope.Id, OwnerId = owner.Id
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application;
    }

    /// <summary>A scope, its owning Scope Admin, and an application they own.</summary>
    public static async Task<(Scope Scope, Person Owner, Application Application)> SeedOwnedApplicationAsync(PostgresFixture db)
    {
        var scope = await SeedScopeAsync(db);
        var owner = await SeedScopeAdminAsync(db, scope);
        var application = await SeedApplicationAsync(db, scope, owner);
        return (scope, owner, application);
    }

    /// <summary>Stores a credential for <paramref name="application" /> and returns it with its secret.</summary>
    public static async Task<(ApplicationCredential Credential, string Secret)> SeedCredentialAsync(
        PostgresFixture db, Application application, DateTime? expiresAt = null, DateTime? revokedAt = null)
    {
        var secret = ApplicationCredentialSecret.New();
        await using var context = db.CreateContext();
        var credential = new ApplicationCredential
        {
            ApplicationId = application.Id,
            SecretHash = ApplicationCredentialSecret.HashOf(secret),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt
        };
        context.ApplicationCredentials.Add(credential);
        await context.SaveChangesAsync();
        return (credential, secret);
    }

    public static async Task<List<ApplicationCredential>> CredentialsOfAsync(PostgresFixture db, Application application)
    {
        await using var context = db.CreateContext();
        return await context.ApplicationCredentials.AsNoTracking()
            .Where(x => x.ApplicationId == application.Id)
            .ToListAsync();
    }
}
```

The Web API test project references the Command project (it already uses Command inputs and outputs); if `ApplicationCredentialSecret` is not reachable, add nothing new — compute the hash with `SingleUseTokenHash.Of` from the same namespace instead.

- [ ] **Step 10: Write the functional tests**

Create `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationCredentialControllerIssueTests.cs`:

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/scopes/{scopeId}/applications/{id}/credentials (UC-53): the main flow
// for a System Admin and for the owning Scope Admin, an optional or offset-less expiry, AF-53a (no live
// application in the scope), AF-53b (a Scope Admin who does not own it), AF-53c (an expiry in the past),
// AF-53d (two live already, including under concurrency), the audit entry, and the framework flows
// (403 for a User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class ApplicationCredentialControllerIssueTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private Task<HttpResponse<DataOutput<IssueApplicationCredentialCommandOutput?>>> IssueAsync(
        Scope scope, Domain.Entities.Application application, IssueApplicationCredentialCommand? body = null) =>
        Gateway.PostAsync<DataOutput<IssueApplicationCredentialCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), body ?? new IssueApplicationCredentialCommand());

    [FunctionalFact]
    public async Task GivenASystemAdmin_WhenIssuing_ThenCreatedWithTheSecretOnceAndOnlyItsHashStored()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = response.Body!.Data!;
        Assert.StartsWith(ApplicationCredentialSecret.Prefix, data.Secret);
        var stored = Assert.Single(await CredentialsOfAsync(db, application));
        Assert.Equal(data.Id, stored.PublicId);
        Assert.Equal(ApplicationCredentialSecret.HashOf(data.Secret), stored.SecretHash);
        Assert.Null(stored.ExpiresAt);
        Assert.Null(stored.RevokedAt);
    }

    [FunctionalFact]
    public async Task GivenTheOwningScopeAdmin_WhenIssuing_ThenCreated()
    {
        // Given
        var (scope, owner, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoBodyAtAll_WhenIssuing_ThenCreatedWithoutExpiry()
    {
        // Given — a JSON POST with nothing in it: the body is optional (EmptyBodyBehavior.Allow)
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        using var empty = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");

        // When
        var response = await Gateway.Client.PostAsync(Route(scope.PublicId, application.PublicId), empty);

        // Then
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(Assert.Single(await CredentialsOfAsync(db, application)).ExpiresAt);
    }

    [FunctionalFact]
    public async Task GivenAnExpiryWithoutAnOffset_WhenIssuing_ThenCreatedAndStoredAsUtc()
    {
        // Given — what a datetime-local input sends
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        using var body = new StringContent(
            "{\"expiresAt\":\"2030-01-01T00:00:00\"}", System.Text.Encoding.UTF8, "application/json");

        // When
        var response = await Gateway.Client.PostAsync(Route(scope.PublicId, application.PublicId), body);

        // Then
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = Assert.Single(await CredentialsOfAsync(db, application));
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), stored.ExpiresAt!.Value.ToUniversalTime());
    }

    [FunctionalFact]
    public async Task GivenAPastExpiry_WhenIssuing_ThenBadRequest()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await IssueAsync(scope, application,
            new IssueApplicationCredentialCommand { ExpiresAt = DateTime.UtcNow.AddMinutes(-5) });

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApplicationCredentialMessages.ExpiryNotInFuture, response.Body!.Errors.Single());
        Assert.Empty(await CredentialsOfAsync(db, application));
    }

    [FunctionalFact]
    public async Task GivenAnApplicationInAnotherScope_WhenIssuing_ThenNotFound()
    {
        // Given
        var (_, _, application) = await SeedOwnedApplicationAsync(db);
        var otherScope = await SeedScopeAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await IssueAsync(otherScope, application);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApplicationMessages.ApplicationNotFound, response.Body!.Errors.Single());
    }

    [FunctionalFact]
    public async Task GivenALogicallyDeletedApplication_WhenIssuing_ThenNotFound()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        await using (var context = db.CreateContext())
        {
            await context.Applications.Where(x => x.Id == application.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
        }
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenACoOwnerOfTheScopeWhoDoesNotOwnTheApplication_WhenIssuing_ThenForbidden()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var coOwner = await SeedScopeAdminAsync(db, scope);
        Authorize(TestTokens.For(coOwner.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApplicationCredentialMessages.NotAuthorizedToManageCredentials, response.Body!.Errors.Single());
        Assert.Empty(await CredentialsOfAsync(db, application));
    }

    [FunctionalFact]
    public async Task GivenTwoLiveCredentials_WhenIssuingAThird_ThenConflict()
    {
        // Given — plus a revoked one, which does not count
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        await SeedCredentialAsync(db, application);
        await SeedCredentialAsync(db, application);
        await SeedCredentialAsync(db, application, revokedAt: DateTime.UtcNow.AddDays(-1));
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApplicationCredentialMessages.CredentialLimitReached, response.Body!.Errors.Single());
        Assert.Equal(3, (await CredentialsOfAsync(db, application)).Count);
    }

    [FunctionalFact]
    public async Task GivenNoCredentials_WhenIssuingInParallel_ThenExactlyTwoAreLive()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // When — eight requests released at the same moment
        var requests = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return await IssueAsync(scope, application);
            }))
            .ToArray();
        start.SetResult();
        var responses = await Task.WhenAll(requests);

        // Then
        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(6, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(2, (await CredentialsOfAsync(db, application)).Count);
    }

    [FunctionalFact]
    public async Task GivenAnIssuedCredential_WhenReadingTheAuditTrail_ThenTheCredentialIsTheTarget()
    {
        // Given
        var (scope, owner, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await IssueAsync(scope, application);

        // Then — the secret is in no audit column
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == nameof(IssueApplicationCredentialCommand) && a.TargetId == response.Body!.Data!.Id);
        Assert.True(entry.Succeeded);
        Assert.Equal(owner.PublicId, entry.ActorPersonId);
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenIssuing_ThenForbidden()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenIssuing_ThenUnauthorized()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

Adjust to the suite's real names where they differ: the `Gateway.PostAsync` return type (`HttpResponse<T>` above stands for whatever `ApplicationControllerUpdateTests` receives — use `var` in the helper if simpler), `TestTokens.For`'s parameter order (`personId, role, scopeId, params ownedScopeIds`), `response.Body.Errors`, and `AuditLog.TargetId`. The logically deleted application can be seeded through `SeedApplicationAsync` plus the update shown, or by adding an `isDeleted` parameter to `ApplicationSeeds.SeedApplicationAsync`.

- [ ] **Step 11: Run the tests, regenerate OpenAPI**

Run, one after the other:
```bash
dotnet build src/ArturRios.Heimdall.sln
dotnet test tests/Application/ArturRios.Heimdall.Shared.Tests --no-build
dotnet test tests/Application/ArturRios.Heimdall.Query.Tests --no-build
dotnet test tests/Application/ArturRios.Heimdall.Command.Tests --no-build
dotnet test tests/Presentation/ArturRios.Heimdall.WebApi.Tests --no-build
python3 scripts/openapi.py
python3 scripts/openapi.py --check
```
Expected: all PASS; the document gains the `credentials` POST. If `OpenApiContractTests` pins an operation count, update its number by one with a comment naming UC-53.

- [ ] **Step 12: Commit**

Stage every file this task created or moved, explicitly (including both sides of the two `git mv`s), plus `Startup.cs`, `docs/openapi/heimdall.json` and any `OpenApiContractTests.cs` change. Subject: `feat: issue application credentials`. Body: UC-53, the access rule shared with UC-17/UC-18, the row-locked limit, `UtcInstant` moving to Shared; then the two trailer lines.

---

### Task 4: List and revoke credentials (UC-54, UC-55), and purge the spent ones

**Files:**
- Create: `src/Application/ArturRios.Heimdall.Query/Input/ListApplicationCredentialsQuery.cs`, `Output/ApplicationCredentialOutput.cs`, `Output/ApplicationCredentialListOutput.cs`, `Handlers/ListApplicationCredentialsQueryHandler.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Input/RevokeApplicationCredentialCommand.cs`, `Output/RevokeApplicationCredentialCommandOutput.cs`, `Handlers/RevokeApplicationCredentialCommandHandler.cs`
- Modify: `src/Application/ArturRios.Heimdall.Shared/Messages/ApplicationCredentialMessageMap.cs`
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/ApplicationCredentialController.cs`, `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`
- Modify: `src/Application/ArturRios.Heimdall.Command/Handlers/PurgeExpiredTokensCommandHandler.cs`, `src/Application/ArturRios.Heimdall.Command/Output/PurgeExpiredTokensCommandOutput.cs`
- Test: `tests/Application/ArturRios.Heimdall.Query.Tests/ListApplicationCredentialsQueryHandlerTests.cs`, `tests/Application/ArturRios.Heimdall.Command.Tests/RevokeApplicationCredentialCommandHandlerTests.cs`, `tests/Application/ArturRios.Heimdall.Command.Tests/PurgeExpiredTokensCommandHandlerTests.cs`, `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationCredentialControllerListTests.cs`, `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationCredentialControllerRevokeTests.cs`, `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/TokenRetentionPurgeTests.cs`

**Interfaces:**
- Consumes: `ApplicationManagementAccess.ResolveAsync`, `ApplicationCredential.IsLiveAt`, `ApplicationCredentialMessages`, `ApplicationSeeds` (Task 3).
- Produces: `ListApplicationCredentialsQuery { ScopeId, Id, ActingPersonId, ActingRole }`; `ApplicationCredentialOutput { Id, CreatedAt, ExpiresAt, RevokedAt, LastUsedAt, Live }`; `ApplicationCredentialListOutput { List<ApplicationCredentialOutput> Credentials }`; `RevokeApplicationCredentialCommand { ScopeId, Id, CredentialId, ActingPersonId, ActingRole }`; `RevokeApplicationCredentialCommandOutput { Id, RevokedAt }`; `PurgeExpiredTokensCommandOutput.ApplicationCredentialsPurged`; controller actions `List` and `Revoke`.

- [ ] **Step 1: Write the failing listing tests**

Create `tests/Application/ArturRios.Heimdall.Query.Tests/ListApplicationCredentialsQueryHandlerTests.cs`:

```csharp
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for ListApplicationCredentialsQueryHandler (UC-54): every credential of the application,
// newest first, each with whether it is live — never a secret or a hash — plus AF-54a (no live
// application in the scope) and AF-54b (a Scope Admin who does not own it).
public class ListApplicationCredentialsQueryHandlerTests
{
    private readonly AsyncFakeRepository<Application, long> _applications = new();
    private readonly AsyncFakeRepository<ApplicationCredential, long> _credentials = new();

    private ListApplicationCredentialsQueryHandler Handler() => new(_applications, _credentials);

    private async Task<Application> SeedApplicationAsync(long id = 10)
    {
        var scope = new Scope { Id = id, PublicId = Guid.NewGuid(), Name = "Acme" };
        var owner = new Person
        {
            Id = id, PublicId = Guid.NewGuid(), Name = "Owner", Email = $"owner-{id}@test.local",
            RoleId = (long)Roles.ScopeAdmin, ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id, Scope = scope }]
        };
        var application = new Application
        {
            Id = id, PublicId = Guid.NewGuid(), Name = "Billing", ScopeId = scope.Id, Scope = scope,
            OwnerId = owner.Id, Owner = owner
        };
        await _applications.CreateAsync(application);
        return application;
    }

    private static ListApplicationCredentialsQuery Query(Application application, int role = (int)Roles.SystemAdmin, Guid? actor = null) => new()
    {
        ScopeId = application.Scope.PublicId, Id = application.PublicId,
        ActingRole = role, ActingPersonId = actor ?? Guid.NewGuid()
    };

    [UnitFact]
    public async Task GivenCredentials_WhenListing_ThenNewestFirstWithLivenessAndNoSecret()
    {
        // Given — one live, one revoked, one expired, and one of another application
        var application = await SeedApplicationAsync();
        var other = await SeedApplicationAsync(id: 11);
        var now = DateTime.UtcNow;
        var oldest = new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a", CreatedAt = now.AddDays(-3), RevokedAt = now.AddDays(-1) };
        var middle = new ApplicationCredential { Id = 2, ApplicationId = application.Id, SecretHash = "b", CreatedAt = now.AddDays(-2), ExpiresAt = now.AddDays(-1) };
        var newest = new ApplicationCredential { Id = 3, ApplicationId = application.Id, SecretHash = "c", CreatedAt = now.AddDays(-1), LastUsedAt = now };
        await _credentials.CreateAsync(oldest);
        await _credentials.CreateAsync(middle);
        await _credentials.CreateAsync(newest);
        await _credentials.CreateAsync(new ApplicationCredential { Id = 4, ApplicationId = other.Id, SecretHash = "d", CreatedAt = now });

        // When
        var output = await Handler().HandleAsync(Query(application));

        // Then
        Assert.True(output.Success);
        Assert.Equal(ApplicationCredentialMessages.CredentialsRetrieved, output.Messages.Single());
        var listed = output.Data!.Credentials;
        Assert.Equal([newest.PublicId, middle.PublicId, oldest.PublicId], listed.Select(c => c.Id));
        Assert.Equal([true, false, false], listed.Select(c => c.Live));
        Assert.Equal(now, listed[0].LastUsedAt);
        Assert.Equal(oldest.RevokedAt, listed[2].RevokedAt);
    }

    [UnitFact]
    public void GivenTheOutputType_WhenInspected_ThenItCarriesNoSecretOrHash()
    {
        // Given / When
        var properties = typeof(ArturRios.Heimdall.Query.Output.ApplicationCredentialOutput)
            .GetProperties().Select(p => p.Name).ToList();

        // Then
        Assert.DoesNotContain(properties, name => name.Contains("Secret") || name.Contains("Hash"));
    }

    [UnitFact]
    public async Task GivenNoCredentials_WhenListing_ThenAnEmptyList()
    {
        // Given
        var application = await SeedApplicationAsync();

        // When
        var output = await Handler().HandleAsync(Query(application));

        // Then
        Assert.True(output.Success);
        Assert.Empty(output.Data!.Credentials);
    }

    [UnitFact]
    public async Task GivenTheWrongScope_WhenListing_ThenApplicationNotFound()
    {
        // Given
        var application = await SeedApplicationAsync();
        var query = Query(application);
        query.ScopeId = Guid.NewGuid();

        // When
        var output = await Handler().HandleAsync(query);

        // Then
        Assert.Equal(ApplicationMessages.ApplicationNotFound, output.Errors.Single());
    }

    [UnitFact]
    public async Task GivenAScopeAdminWhoDoesNotOwnIt_WhenListing_ThenNotAuthorized()
    {
        // Given
        var application = await SeedApplicationAsync();

        // When
        var output = await Handler().HandleAsync(Query(application, (int)Roles.ScopeAdmin));

        // Then
        Assert.Equal(ApplicationCredentialMessages.NotAuthorizedToManageCredentials, output.Errors.Single());
    }
}
```

Run: `dotnet build src/ArturRios.Heimdall.sln` — Expected: build FAILS on the missing query types.

- [ ] **Step 2: Write the query, outputs and handler**

`src/Application/ArturRios.Heimdall.Query/Input/ListApplicationCredentialsQuery.cs`:

```csharp
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>Lists an application's credentials (UC-54, FR-AP-12). Every field is set by the controller.</summary>
public class ListApplicationCredentialsQuery : BaseQuery, IActorScoped
{
    public Guid ScopeId { get; set; }

    public Guid Id { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
```

`src/Application/ArturRios.Heimdall.Query/Output/ApplicationCredentialOutput.cs`:

```csharp
namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     One credential in UC-54's listing. Never the secret, which is shown once by UC-53, and never
///     its hash.
/// </summary>
public class ApplicationCredentialOutput
{
    /// <summary>The credential's <c>PublicId</c> — UC-55's route value and the token's <c>credentialId</c>.</summary>
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>When a token was last issued from it (UC-56), or <c>null</c> if never.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Whether UC-56 would accept it now: not revoked and not expired.</summary>
    public bool Live { get; set; }
}
```

`src/Application/ArturRios.Heimdall.Query/Output/ApplicationCredentialListOutput.cs`:

```csharp
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     UC-54's answer: every credential the application holds, newest first. Not paginated — an
///     application holds at most two live credentials, and spent ones are purged after the
///     single-use-token grace, so the list stays a handful of rows.
/// </summary>
public class ApplicationCredentialListOutput : QueryOutput
{
    public List<ApplicationCredentialOutput> Credentials { get; set; } = [];
}
```

`src/Application/ArturRios.Heimdall.Query/Handlers/ListApplicationCredentialsQueryHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>Handles <see cref="ListApplicationCredentialsQuery" /> (UC-54, FR-AP-12).</summary>
public class ListApplicationCredentialsQueryHandler(
    IAsyncReadOnlyRepository<Application, long> applicationReader,
    IAsyncReadOnlyRepository<ApplicationCredential, long> credentialReader)
    : IQueryHandlerAsync<ListApplicationCredentialsQuery, ApplicationCredentialListOutput>
{
    public async Task<DataOutput<ApplicationCredentialListOutput?>> HandleAsync(
        ListApplicationCredentialsQuery query, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<ApplicationCredentialListOutput?>.New;

        // AF-54a, then AF-54b.
        var access = await ApplicationManagementAccess.ResolveAsync(
            applicationReader.Query(), query.ScopeId, query.Id, query.ActingRole, query.ActingPersonId);

        switch (access.Access)
        {
            case ApplicationAccess.NotFound:
                return output.WithError(ApplicationMessages.ApplicationNotFound);
            case ApplicationAccess.Forbidden:
                return output.WithError(ApplicationCredentialMessages.NotAuthorizedToManageCredentials);
        }

        var applicationId = access.Application!.Id;
        var credentials = await credentialReader.Query()
            .Where(x => x.ApplicationId == applicationId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        // Liveness is judged once, in memory, so every row is measured against the same instant.
        var now = DateTime.UtcNow;

        return output
            .WithData(new ApplicationCredentialListOutput
            {
                Credentials = credentials.Select(x => new ApplicationCredentialOutput
                {
                    Id = x.PublicId,
                    CreatedAt = x.CreatedAt,
                    ExpiresAt = x.ExpiresAt,
                    RevokedAt = x.RevokedAt,
                    LastUsedAt = x.LastUsedAt,
                    Live = x.IsLiveAt(now)
                }).ToList()
            })
            .WithMessage(ApplicationCredentialMessages.CredentialsRetrieved);
    }
}
```

Run: `dotnet build src/ArturRios.Heimdall.sln && dotnet test tests/Application/ArturRios.Heimdall.Query.Tests --no-build --filter "FullyQualifiedName~ListApplicationCredentials"` — Expected: PASS (5).

- [ ] **Step 3: Write the failing revoke tests**

Create `tests/Application/ArturRios.Heimdall.Command.Tests/RevokeApplicationCredentialCommandHandlerTests.cs`, seeding as in Step 1 (copy its `SeedApplicationAsync`, adapted to this class) with:

```csharp
// Unit tests for RevokeApplicationCredentialCommandHandler (UC-55): the main flow stamps RevokedAt
// and returns the credential as the audit target; AF-55a (no live application in the scope), AF-55b
// (a Scope Admin who does not own it), AF-55c (no such credential on this application — including one
// of another application) and AF-55d (already revoked). An expired credential can still be revoked.
public class RevokeApplicationCredentialCommandHandlerTests
{
    private readonly AsyncFakeRepository<Application, long> _applications = new();
    private readonly AsyncFakeRepository<ApplicationCredential, long> _credentials = new();

    private RevokeApplicationCredentialCommandHandler Handler() => new(_applications, _credentials, _credentials);

    // SeedApplicationAsync(long id = 10) — as in ListApplicationCredentialsQueryHandlerTests.

    private static RevokeApplicationCredentialCommand Command(
        Application application, Guid credentialId, int role = (int)Roles.SystemAdmin, Guid? actor = null) => new()
    {
        ScopeId = application.Scope.PublicId, Id = application.PublicId, CredentialId = credentialId,
        ActingRole = role, ActingPersonId = actor ?? Guid.NewGuid()
    };

    [UnitFact]
    public async Task GivenALiveCredential_WhenRevoking_ThenRevokedAtIsStampedAndItIsTheTarget()
    {
        // Given
        var application = await SeedApplicationAsync();
        var credential = new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a", CreatedAt = DateTime.UtcNow };
        await _credentials.CreateAsync(credential);

        // When
        var output = await Handler().HandleAsync(Command(application, credential.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Equal(ApplicationCredentialMessages.CredentialRevoked, output.Messages.Single());
        Assert.Equal(credential.PublicId, output.Data!.Id);
        var stored = Assert.Single(_credentials.Query());
        Assert.NotNull(stored.RevokedAt);
        Assert.Equal(stored.RevokedAt, output.Data.RevokedAt);
    }

    [UnitFact]
    public async Task GivenAnExpiredCredential_WhenRevoking_ThenItIsRevoked()
    {
        // Given
        var application = await SeedApplicationAsync();
        var credential = new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a", ExpiresAt = DateTime.UtcNow.AddDays(-1) };
        await _credentials.CreateAsync(credential);

        // When
        var output = await Handler().HandleAsync(Command(application, credential.PublicId));

        // Then
        Assert.True(output.Success);
    }

    [UnitFact]
    public async Task GivenARevokedCredential_WhenRevoking_ThenAlreadyRevokedAndTheStampIsKept()
    {
        // Given
        var application = await SeedApplicationAsync();
        var revokedAt = DateTime.UtcNow.AddDays(-1);
        var credential = new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a", RevokedAt = revokedAt };
        await _credentials.CreateAsync(credential);

        // When
        var output = await Handler().HandleAsync(Command(application, credential.PublicId));

        // Then
        Assert.Equal(ApplicationCredentialMessages.CredentialAlreadyRevoked, output.Errors.Single());
        Assert.Equal(revokedAt, Assert.Single(_credentials.Query()).RevokedAt);
    }

    [UnitFact]
    public async Task GivenACredentialOfAnotherApplication_WhenRevoking_ThenCredentialNotFound()
    {
        // Given
        var application = await SeedApplicationAsync();
        var other = await SeedApplicationAsync(id: 11);
        var credential = new ApplicationCredential { Id = 1, ApplicationId = other.Id, SecretHash = "a" };
        await _credentials.CreateAsync(credential);

        // When
        var output = await Handler().HandleAsync(Command(application, credential.PublicId));

        // Then
        Assert.Equal(ApplicationCredentialMessages.CredentialNotFound, output.Errors.Single());
        Assert.Null(Assert.Single(_credentials.Query()).RevokedAt);
    }

    [UnitFact]
    public async Task GivenTheWrongScope_WhenRevoking_ThenApplicationNotFound()
    {
        // Given
        var application = await SeedApplicationAsync();
        var command = Command(application, Guid.NewGuid());
        command.ScopeId = Guid.NewGuid();

        // When
        var output = await Handler().HandleAsync(command);

        // Then
        Assert.Equal(ApplicationMessages.ApplicationNotFound, output.Errors.Single());
    }

    [UnitFact]
    public async Task GivenAScopeAdminWhoDoesNotOwnIt_WhenRevoking_ThenNotAuthorized()
    {
        // Given
        var application = await SeedApplicationAsync();
        var credential = new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a" };
        await _credentials.CreateAsync(credential);

        // When
        var output = await Handler().HandleAsync(Command(application, credential.PublicId, (int)Roles.ScopeAdmin));

        // Then
        Assert.Equal(ApplicationCredentialMessages.NotAuthorizedToManageCredentials, output.Errors.Single());
        Assert.Null(Assert.Single(_credentials.Query()).RevokedAt);
    }
}
```

Run: `dotnet build src/ArturRios.Heimdall.sln` — Expected: build FAILS on the missing command types.

- [ ] **Step 4: Write the revoke command, output and handler**

`src/Application/ArturRios.Heimdall.Command/Input/RevokeApplicationCredentialCommand.cs`: a `BaseCommand, IActorScoped` with `[JsonIgnore]` `ScopeId`, `Id`, `CredentialId`, `ActingPersonId`, `ActingRole`, and a summary "Revokes one of an application's credentials (UC-55, FR-AP-12). Every field comes from the route or the actor."

`src/Application/ArturRios.Heimdall.Command/Output/RevokeApplicationCredentialCommandOutput.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>UC-55's answer. <see cref="Id" /> is the credential's <c>PublicId</c> and the audit target.</summary>
public class RevokeApplicationCredentialCommandOutput : CommandOutput
{
    public Guid Id { get; set; }

    public DateTime RevokedAt { get; set; }
}
```

`src/Application/ArturRios.Heimdall.Command/Handlers/RevokeApplicationCredentialCommandHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="RevokeApplicationCredentialCommand" /> (UC-55, FR-AP-12): stamps
///     <c>RevokedAt</c>. From then on UC-56 refuses the secret and UC-57 refuses every token already
///     issued from it — revocation takes effect at the next live re-check, not at token expiry.
/// </summary>
public class RevokeApplicationCredentialCommandHandler(
    IAsyncReadOnlyRepository<Application, long> applicationReader,
    IAsyncReadOnlyRepository<ApplicationCredential, long> credentialReader,
    IAsyncRepository<ApplicationCredential, long> credentialWriter)
    : ICommandHandlerAsync<RevokeApplicationCredentialCommand, RevokeApplicationCredentialCommandOutput>
{
    public async Task<DataOutput<RevokeApplicationCredentialCommandOutput?>> HandleAsync(
        RevokeApplicationCredentialCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<RevokeApplicationCredentialCommandOutput?>.New;

        // AF-55a, then AF-55b.
        var access = await ApplicationManagementAccess.ResolveAsync(
            applicationReader.Query(), command.ScopeId, command.Id, command.ActingRole, command.ActingPersonId);

        switch (access.Access)
        {
            case ApplicationAccess.NotFound:
                return output.WithError(ApplicationMessages.ApplicationNotFound);
            case ApplicationAccess.Forbidden:
                return output.WithError(ApplicationCredentialMessages.NotAuthorizedToManageCredentials);
        }

        // AF-55c: the credential must belong to this application; one of another is not the
        // resource this route addresses.
        var applicationId = access.Application!.Id;
        var credential = await credentialReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.CredentialId && x.ApplicationId == applicationId,
                cancellationToken);

        if (credential is null)
        {
            return output.WithError(ApplicationCredentialMessages.CredentialNotFound);
        }

        // AF-55d: the first revocation's time stands.
        if (credential.RevokedAt is not null)
        {
            return output.WithError(ApplicationCredentialMessages.CredentialAlreadyRevoked);
        }

        credential.RevokedAt = DateTime.UtcNow;

        var update = await credentialWriter.UpdateAsync(credential);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        return output
            .WithData(new RevokeApplicationCredentialCommandOutput
            {
                Id = credential.PublicId,
                RevokedAt = credential.RevokedAt.Value
            })
            .WithMessage(ApplicationCredentialMessages.CredentialRevoked);
    }
}
```

Run: `dotnet build src/ArturRios.Heimdall.sln && dotnet test tests/Application/ArturRios.Heimdall.Command.Tests --no-build --filter "FullyQualifiedName~RevokeApplicationCredential"` — Expected: PASS (6).

- [ ] **Step 5: Map the messages, register, and add the actions**

In `ApplicationCredentialMessageMap`, add:

```csharp
            // UC-54 main flow and its empty case — credentials listed.
            [ApplicationCredentialMessages.CredentialsRetrieved] = HttpStatusCodes.Ok,
            // UC-55 main flow — credential revoked.
            [ApplicationCredentialMessages.CredentialRevoked] = HttpStatusCodes.Ok,
            // AF-55c — no such credential on this application.
            [ApplicationCredentialMessages.CredentialNotFound] = HttpStatusCodes.NotFound,
            // AF-55d — already revoked.
            [ApplicationCredentialMessages.CredentialAlreadyRevoked] = HttpStatusCodes.Conflict,
```

and extend the comments on the shared rows to cite AF-54a/AF-55a and AF-54b/AF-55b. Update the map test if Task 3 added one.

In `Startup.AddDependencies`, beside UC-53's registration:

```csharp
        Builder.Services.AddAuditedCommandHandler<RevokeApplicationCredentialCommand, RevokeApplicationCredentialCommandOutput, RevokeApplicationCredentialCommandHandler>();
```

and beside the application queries:

```csharp
        Builder.Services
            .AddScoped<IQueryHandlerAsync<ListApplicationCredentialsQuery, ApplicationCredentialListOutput>,
                ListApplicationCredentialsQueryHandler>();
```

In `ApplicationCredentialController` (add `using ArturRios.Heimdall.Query.Input;` and `using ArturRios.Heimdall.Query.Output;`):

```csharp
    /// <summary>
    ///     Lists the application's credentials, newest first, with whether each is live (UC-54).
    ///     Never a secret or a hash.
    /// </summary>
    [HttpGet]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<ApplicationCredentialListOutput?>>> List(Guid scopeId, Guid id)
    {
        var query = new ListApplicationCredentialsQuery { ScopeId = scopeId, Id = id };
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecuteQueryAsync<ListApplicationCredentialsQuery, ApplicationCredentialListOutput>(query);

        return result.ToActionResult(statusMap: ApplicationCredentialMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Revokes one credential (UC-55). Its secret stops being exchanged at once, and every token
    ///     already issued from it fails the next live re-check (UC-57).
    /// </summary>
    [HttpDelete("{credentialId:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<RevokeApplicationCredentialCommandOutput?>>> Revoke(
        Guid scopeId, Guid id, Guid credentialId)
    {
        var command = new RevokeApplicationCredentialCommand { ScopeId = scopeId, Id = id, CredentialId = credentialId };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<RevokeApplicationCredentialCommand, RevokeApplicationCredentialCommandOutput>(command);

        return result.ToActionResult(statusMap: ApplicationCredentialMessageMap.StatusCodes);
    }
```

- [ ] **Step 6: Write the functional tests**

Create `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationCredentialControllerListTests.cs` (same header shape as Task 3's file, `using static …ApplicationSeeds;`) with these tests, each seeding through `ApplicationSeeds`:

- `GivenCredentials_WhenListingAsSystemAdmin_ThenNewestFirstWithLivenessAndNoSecret` — seed one revoked then one live credential; `GET Route(...)`; assert `200`, the two ids newest first, `Live` `[true, false]`, and that the raw body string (read with `Gateway.Client.GetStringAsync`, or from the response content) contains neither seeded secret nor `"secretHash"`.
- `GivenTheOwningScopeAdmin_WhenListing_ThenOk`.
- `GivenNoCredentials_WhenListing_ThenOkWithAnEmptyList`.
- `GivenAnApplicationInAnotherScope_WhenListing_ThenNotFound`.
- `GivenACoOwnerOfTheScopeWhoDoesNotOwnTheApplication_WhenListing_ThenForbidden`.
- `GivenAUser_WhenListing_ThenForbidden` and `GivenNoToken_WhenListing_ThenUnauthorized`.

Create `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationCredentialControllerRevokeTests.cs` with:

- `GivenALiveCredential_WhenRevoking_ThenOkAndRevokedAtIsStored` — `DELETE Route(...)/{credential.PublicId}`; assert `200`, `Data.Id`, and the stored `RevokedAt` is not null.
- `GivenARevokedCredential_WhenRevokingAgain_ThenConflict` — `409 CredentialAlreadyRevoked`.
- `GivenACredentialOfAnotherApplication_WhenRevoking_ThenNotFound` — seed a second owned application with a credential; revoke it through the first application's route; `404 CredentialNotFound`; the credential is still unrevoked.
- `GivenAnUnknownCredential_WhenRevoking_ThenNotFound`.
- `GivenACoOwnerOfTheScopeWhoDoesNotOwnTheApplication_WhenRevoking_ThenForbidden`.
- `GivenARevocation_WhenReadingTheAuditTrail_ThenTheCredentialIsTheTarget` — the entry with `Action == nameof(RevokeApplicationCredentialCommand)` and `TargetId == credential.PublicId` exists and succeeded.
- `GivenAUser_WhenRevoking_ThenForbidden` and `GivenNoToken_WhenRevoking_ThenUnauthorized`.

Write each with full Given/When/Then bodies in the style of Task 3's file.

- [ ] **Step 7: Write the failing purge tests**

In `tests/Application/ArturRios.Heimdall.Command.Tests/PurgeExpiredTokensCommandHandlerTests.cs`, extend the handler factory with an `AsyncFakeRepository<ApplicationCredential, long>` (passed as reader and writer, after the pending-registration pair) and add:

```csharp
    [UnitFact]
    public async Task GivenSpentCredentialsPastTheGrace_WhenPurging_ThenOnlyThoseAreRemoved()
    {
        // Given — revoked past the grace, expired past the grace, revoked within it, and live
        var cutoff = DateTime.UtcNow - Retention.SingleUseTokenGrace;
        var credentials = new AsyncFakeRepository<ApplicationCredential, long>();
        await credentials.CreateAsync(new ApplicationCredential { Id = 1, SecretHash = "a", RevokedAt = cutoff.AddMinutes(-1) });
        await credentials.CreateAsync(new ApplicationCredential { Id = 2, SecretHash = "b", ExpiresAt = cutoff.AddMinutes(-1) });
        await credentials.CreateAsync(new ApplicationCredential { Id = 3, SecretHash = "c", RevokedAt = cutoff.AddMinutes(5) });
        await credentials.CreateAsync(new ApplicationCredential { Id = 4, SecretHash = "d" });

        // When
        var output = await Handler(credentials: credentials).HandleAsync(new PurgeExpiredTokensCommand());

        // Then
        Assert.Equal(2, output.Data!.ApplicationCredentialsPurged);
        Assert.Equal([3L, 4L], credentials.Query().Select(c => c.Id).OrderBy(id => id));
    }
```

Use the file's own names for the retention options and the handler factory (`Retention`, `Handler(...)` above stand for whatever it already has); every existing construction of the handler gains the new pair.

Run: `dotnet build src/ArturRios.Heimdall.sln` — Expected: build FAILS (constructor and output field missing).

- [ ] **Step 8: Purge spent credentials**

In `PurgeExpiredTokensCommandHandler`, add `IAsyncReadOnlyRepository<ApplicationCredential, long> applicationCredentialReader` and `IAsyncRepository<ApplicationCredential, long> applicationCredentialWriter` after the pending-registration pair, and after the pending-registration purge:

```csharp
        // UC-55: a revoked or expired credential can never be accepted again, so after the same
        // grace as the single-use tokens it is removed — revoked or expired, whichever came first.
        var applicationCredentials = await PurgeAsync(
            applicationCredentialReader.Query()
                .Where(credential => (credential.RevokedAt != null && credential.RevokedAt <= cutoff) ||
                                     (credential.ExpiresAt != null && credential.ExpiresAt <= cutoff))
                .OrderBy(credential => credential.RevokedAt ?? credential.ExpiresAt),
            applicationCredentialWriter);
```

Concatenate `applicationCredentials.Errors` into `errors`, and set `ApplicationCredentialsPurged = applicationCredentials.Count` on the output. In `PurgeExpiredTokensCommandOutput`, add `public int ApplicationCredentialsPurged { get; set; }` with a summary citing UC-55. Extend the handler's class summary/remarks to name the credentials table.

In `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/TokenRetentionPurgeTests.cs`, add one functional test in that file's style: a credential revoked well past the grace and a live one; after the pass runs (as the file's other tests trigger it), only the live one remains.

Run, one at a time: `dotnet test tests/Application/ArturRios.Heimdall.Command.Tests --no-build`, then the Web API project. Expected: PASS.

- [ ] **Step 9: Run every project, regenerate OpenAPI**

As Task 3 Step 11 (Shared, Query, Command, Web API in turn, then `python3 scripts/openapi.py` and `--check`). Expected: PASS; the document gains `GET …/credentials` and `DELETE …/credentials/{credentialId}`.

- [ ] **Step 10: Commit**

Stage every file this task created or changed, explicitly, plus `docs/openapi/heimdall.json`. Subject: `feat: list and revoke application credentials`. Body: UC-54, UC-55, revocation effective at the next re-check, the purge; then the two trailer lines.

---

### Task 5: `POST /api/applications/token` — obtain an application token (UC-56)

**Files:**
- Create: `src/Application/ArturRios.Heimdall.Shared/Messages/ApplicationTokenMessages.cs`, `ApplicationTokenMessageMap.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Input/IssueApplicationTokenCommand.cs`, `Input/Validation/IssueApplicationTokenCommandValidator.cs`, `Output/IssueApplicationTokenCommandOutput.cs`, `Handlers/IssueApplicationTokenCommandHandler.cs`
- Create: `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/ApplicationTokenController.cs`
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`
- Test: `tests/Application/ArturRios.Heimdall.Command.Tests/IssueApplicationTokenCommandValidatorTests.cs`, `tests/Application/ArturRios.Heimdall.Command.Tests/IssueApplicationTokenCommandHandlerTests.cs`, `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationTokenControllerIssueTests.cs`

**Interfaces:**
- Consumes: `ApplicationCredential.LiveAt`, `ApplicationCredentialSecret.HashOf`, `IApplicationTokenIssuer`/`ApplicationTokenSubject`/`AuthToken` (Task 2), `Startup.AuthEndpointRateLimitPolicy`, `ApplicationSeeds` (Task 3).
- Produces: `ApplicationTokenMessages` (all eight texts of Decision 3's second table — declare them all now; Task 6 maps the remaining two); `ApplicationTokenMessageMap.StatusCodes`; `IssueApplicationTokenCommand { ApplicationId, ScopeId, Secret }`; `IssueApplicationTokenCommandOutput { Id, Token, ExpiresAt }`; `ApplicationTokenController` at `api/applications` with action `IssueToken`.

- [ ] **Step 1: Write the messages and the map**

`src/Application/ArturRios.Heimdall.Shared/Messages/ApplicationTokenMessages.cs`, one `public const string` per row of Decision 3's second table, each with a one-line summary citing its UC/AF:

| Constant | Summary |
| --- | --- |
| `TokenIssued` | UC-56 main flow. |
| `TokenAccepted` | UC-57 main flow. |
| `ApplicationIdRequired`, `ScopeIdRequired`, `SecretRequired`, `SecretTooLong` | AF-56a — the request is malformed. |
| `CredentialsNotAccepted` | AF-56b — every refusal, whatever failed: one message so the endpoint reveals nothing. |
| `TokenNotAccepted` | AF-57a — every refusal of the re-check, for the same reason. |

`src/Application/ArturRios.Heimdall.Shared/Messages/ApplicationTokenMessageMap.cs`, in `ApplicationCredentialMessageMap`'s shape, mapping now:

```csharp
            // UC-56 main flow — token issued.
            [ApplicationTokenMessages.TokenIssued] = HttpStatusCodes.Ok,
            // AF-56a — a field is missing or the secret is over-long.
            [ApplicationTokenMessages.ApplicationIdRequired] = HttpStatusCodes.BadRequest,
            [ApplicationTokenMessages.ScopeIdRequired] = HttpStatusCodes.BadRequest,
            [ApplicationTokenMessages.SecretRequired] = HttpStatusCodes.BadRequest,
            [ApplicationTokenMessages.SecretTooLong] = HttpStatusCodes.BadRequest,
            // AF-56b — any refusal: unknown, revoked or expired secret, another application's, a
            // deleted application or scope, or the wrong scope. One status, one message.
            [ApplicationTokenMessages.CredentialsNotAccepted] = HttpStatusCodes.Unauthorized
```

- [ ] **Step 2: Write the failing validator and handler tests**

`tests/Application/ArturRios.Heimdall.Command.Tests/IssueApplicationTokenCommandValidatorTests.cs`:

```csharp
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for IssueApplicationTokenCommandValidator (UC-56 AF-56a): the three fields are required
// and the secret is bounded. Nothing here looks at whether the secret is right — that is AF-56b's
// single refusal, so a malformed request is the only thing a 400 can say.
public class IssueApplicationTokenCommandValidatorTests
{
    private static readonly IssueApplicationTokenCommandValidator Validator = new();

    private static IssueApplicationTokenCommand Valid() => new()
    {
        ApplicationId = Guid.NewGuid(), ScopeId = Guid.NewGuid(), Secret = "hds_" + new string('a', 43)
    };

    [UnitFact]
    public async Task GivenAllFields_WhenValidating_ThenItIsValid()
    {
        // Given / When
        var result = await Validator.ValidateAsync(Valid());

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenNothing_WhenValidating_ThenEachMissingFieldIsNamed()
    {
        // Given / When
        var result = await Validator.ValidateAsync(new IssueApplicationTokenCommand());

        // Then
        Assert.Equal(
            [ApplicationTokenMessages.ApplicationIdRequired, ApplicationTokenMessages.ScopeIdRequired, ApplicationTokenMessages.SecretRequired],
            result.Errors.Select(e => e.ErrorMessage));
    }

    [UnitFact]
    public async Task GivenAnOverlongSecret_WhenValidating_ThenItIsRefused()
    {
        // Given
        var command = Valid();
        command.Secret = new string('a', 101);

        // When
        var result = await Validator.ValidateAsync(command);

        // Then
        Assert.Equal([ApplicationTokenMessages.SecretTooLong], result.Errors.Select(e => e.ErrorMessage));
    }
}
```

`tests/Application/ArturRios.Heimdall.Command.Tests/IssueApplicationTokenCommandHandlerTests.cs`:

```csharp
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for IssueApplicationTokenCommandHandler (UC-56): the main flow issues a token for the
// application, scope and credential, stamps LastUsedAt and returns the application as the audit
// target; every refusal (AF-56b) is the same single message, whichever check failed.
public class IssueApplicationTokenCommandHandlerTests
{
    private readonly AsyncFakeRepository<ApplicationCredential, long> _credentials = new();
    private readonly Mock<IApplicationTokenIssuer> _issuer = new();
    private readonly DateTime _expiresAt = DateTime.UtcNow.AddMinutes(10);

    public IssueApplicationTokenCommandHandlerTests()
    {
        _issuer.Setup(i => i.IssueAsync(It.IsAny<ApplicationTokenSubject>()))
            .ReturnsAsync(new AuthToken("signed.application.token", _expiresAt));
    }

    private IssueApplicationTokenCommandHandler Handler()
    {
        var validator = new Mock<IValidator<IssueApplicationTokenCommand>>();
        validator.Setup(v => v.ValidateAsync(It.IsAny<IssueApplicationTokenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        return new IssueApplicationTokenCommandHandler(validator.Object, _credentials, _credentials, _issuer.Object);
    }

    private async Task<(ApplicationCredential Credential, string Secret)> SeedAsync(
        bool applicationDeleted = false, bool scopeDeleted = false,
        DateTime? expiresAt = null, DateTime? revokedAt = null)
    {
        var scope = new Scope { Id = 1, PublicId = Guid.NewGuid(), Name = "Acme", IsDeleted = scopeDeleted };
        var application = new Application
        {
            Id = 1, PublicId = Guid.NewGuid(), Name = "Billing Service", IsDeleted = applicationDeleted,
            ScopeId = scope.Id, Scope = scope
        };
        var secret = ApplicationCredentialSecret.New();
        var credential = new ApplicationCredential
        {
            Id = 1, ApplicationId = application.Id, Application = application,
            SecretHash = ApplicationCredentialSecret.HashOf(secret), CreatedAt = DateTime.UtcNow.AddDays(-1),
            ExpiresAt = expiresAt, RevokedAt = revokedAt
        };
        await _credentials.CreateAsync(credential);
        return (credential, secret);
    }

    private static IssueApplicationTokenCommand Command(ApplicationCredential credential, string secret) => new()
    {
        ApplicationId = credential.Application.PublicId,
        ScopeId = credential.Application.Scope.PublicId,
        Secret = secret
    };

    [UnitFact]
    public async Task GivenALiveCredential_WhenExchanging_ThenATokenIsIssuedAndLastUsedAtStamped()
    {
        // Given
        var (credential, secret) = await SeedAsync();

        // When
        var output = await Handler().HandleAsync(Command(credential, secret));

        // Then
        Assert.True(output.Success);
        Assert.Equal(ApplicationTokenMessages.TokenIssued, output.Messages.Single());
        Assert.Equal("signed.application.token", output.Data!.Token);
        Assert.Equal(_expiresAt, output.Data.ExpiresAt);
        Assert.Equal(credential.Application.PublicId, output.Data.Id);
        _issuer.Verify(i => i.IssueAsync(new ApplicationTokenSubject(
            credential.Application.PublicId, credential.Application.Scope.PublicId,
            credential.PublicId, "Billing Service")), Times.Once);
        Assert.NotNull(Assert.Single(_credentials.Query()).LastUsedAt);
    }

    [UnitFact]
    public async Task GivenASecretWithSurroundingWhitespace_WhenExchanging_ThenATokenIsIssued()
    {
        // Given — read from a file, with its trailing newline
        var (credential, secret) = await SeedAsync();

        // When
        var output = await Handler().HandleAsync(Command(credential, $"  {secret}\n"));

        // Then
        Assert.True(output.Success);
    }

    public static TheoryData<string> Refusals => new()
    {
        "wrong-secret", "other-application", "other-scope", "revoked", "expired", "application-deleted", "scope-deleted"
    };

    [UnitTheory]
    [MemberData(nameof(Refusals))]
    public async Task GivenAnyRefusal_WhenExchanging_ThenTheSameMessageAndNoToken(string refusal)
    {
        // Given
        var (credential, secret) = await SeedAsync(
            applicationDeleted: refusal == "application-deleted",
            scopeDeleted: refusal == "scope-deleted",
            expiresAt: refusal == "expired" ? DateTime.UtcNow.AddSeconds(-1) : null,
            revokedAt: refusal == "revoked" ? DateTime.UtcNow.AddSeconds(-1) : null);
        var command = Command(credential, secret);
        switch (refusal)
        {
            case "wrong-secret": command.Secret = ApplicationCredentialSecret.New(); break;
            case "other-application": command.ApplicationId = Guid.NewGuid(); break;
            case "other-scope": command.ScopeId = Guid.NewGuid(); break;
        }

        // When
        var output = await Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Equal([ApplicationTokenMessages.CredentialsNotAccepted], output.Errors);
        Assert.Null(output.Data);
        _issuer.Verify(i => i.IssueAsync(It.IsAny<ApplicationTokenSubject>()), Times.Never);
        Assert.Null(Assert.Single(_credentials.Query()).LastUsedAt);
    }
}
```

`ApplicationTokenSubject` is a record, so `Verify` with a new instance compares by value. Use `[UnitTheory]` with `[MemberData]` only if the suite's theory attribute supports it (check another `MemberData` use); otherwise list the seven cases as `[InlineData("…")]`.

Run: `dotnet build src/ArturRios.Heimdall.sln` — Expected: build FAILS on the missing command types.

- [ ] **Step 3: Write the command, validator, output and handler**

`src/Application/ArturRios.Heimdall.Command/Input/IssueApplicationTokenCommand.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Exchanges an application's secret for an application token (UC-56, FR-AP-13). Anonymous: the
///     secret is the credential. All three fields must name one live credential of one live
///     application in one live scope.
/// </summary>
public class IssueApplicationTokenCommand : BaseCommand
{
    /// <summary>The application's <c>PublicId</c>.</summary>
    public Guid ApplicationId { get; set; }

    /// <summary>The <c>PublicId</c> of the application's scope.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>A secret UC-53 issued for that application.</summary>
    public string Secret { get; set; } = string.Empty;
}
```

`src/Application/ArturRios.Heimdall.Command/Input/Validation/IssueApplicationTokenCommandValidator.cs`:

```csharp
using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>UC-56 AF-56a: the request names an application, a scope and a secret of bounded length.</summary>
public class IssueApplicationTokenCommandValidator : AbstractValidator<IssueApplicationTokenCommand>
{
    /// <summary>A secret is 47 characters; the bound only stops hashing an arbitrarily large body.</summary>
    private const int MaxSecretLength = 100;

    public IssueApplicationTokenCommandValidator()
    {
        RuleFor(command => command.ApplicationId)
            .NotEmpty().WithMessage(ApplicationTokenMessages.ApplicationIdRequired);

        RuleFor(command => command.ScopeId)
            .NotEmpty().WithMessage(ApplicationTokenMessages.ScopeIdRequired);

        RuleFor(command => command.Secret)
            .NotEmpty().WithMessage(ApplicationTokenMessages.SecretRequired)
            .MaximumLength(MaxSecretLength).WithMessage(ApplicationTokenMessages.SecretTooLong);
    }
}
```

`src/Application/ArturRios.Heimdall.Command/Output/IssueApplicationTokenCommandOutput.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     UC-56's answer. <see cref="Id" /> is the application's <c>PublicId</c> — the one the caller
///     sent — and the audit target.
/// </summary>
public class IssueApplicationTokenCommandOutput : CommandOutput
{
    public Guid Id { get; set; }

    /// <summary>The application token, valid until <see cref="ExpiresAt" />.</summary>
    public string Token { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
}
```

`src/Application/ArturRios.Heimdall.Command/Handlers/IssueApplicationTokenCommandHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="IssueApplicationTokenCommand" /> (UC-56, FR-AP-13/14): finds the live
///     credential by the hash of the presented secret, checks it belongs to the named application in
///     the named scope, both live, and issues a token.
/// </summary>
/// <remarks>
///     <para>
///         <b>One refusal.</b> An unknown secret, a revoked or expired one, another application's, a
///         deleted application or scope, and the wrong scope all answer
///         <see cref="ApplicationTokenMessages.CredentialsNotAccepted" />, so the endpoint says
///         nothing about which applications, scopes or credentials exist (the stance UC-11 takes for
///         persons). The audit entry of a refusal therefore has no target.
///     </para>
///     <para>
///         <b>Whitespace.</b> A secret has none, so it is trimmed: one read from a file arrives with
///         its trailing newline, and refusing it would be a failure nobody could diagnose from one
///         uniform 401.
///     </para>
/// </remarks>
public class IssueApplicationTokenCommandHandler(
    IValidator<IssueApplicationTokenCommand> validator,
    IAsyncReadOnlyRepository<ApplicationCredential, long> credentialReader,
    IAsyncRepository<ApplicationCredential, long> credentialWriter,
    IApplicationTokenIssuer issuer)
    : ICommandHandlerAsync<IssueApplicationTokenCommand, IssueApplicationTokenCommandOutput>
{
    public async Task<DataOutput<IssueApplicationTokenCommandOutput?>> HandleAsync(
        IssueApplicationTokenCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<IssueApplicationTokenCommandOutput?>.New;

        // AF-56a.
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        var now = DateTime.UtcNow;
        var hash = ApplicationCredentialSecret.HashOf(command.Secret.Trim());

        var credential = await credentialReader.Query()
            .Include(x => x.Application)
            .ThenInclude(application => application.Scope)
            .Where(ApplicationCredential.LiveAt(now))
            .FirstOrDefaultAsync(x => x.SecretHash == hash, cancellationToken);

        // AF-56b — every check, one answer.
        if (credential is null ||
            credential.Application.PublicId != command.ApplicationId ||
            credential.Application.IsDeleted ||
            credential.Application.Scope.PublicId != command.ScopeId ||
            credential.Application.Scope.IsDeleted)
        {
            return output.WithError(ApplicationTokenMessages.CredentialsNotAccepted);
        }

        var application = credential.Application;
        var token = await issuer.IssueAsync(new ApplicationTokenSubject(
            application.PublicId, application.Scope.PublicId, credential.PublicId, application.Name));

        credential.LastUsedAt = now;

        var update = await credentialWriter.UpdateAsync(credential);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        return output
            .WithData(new IssueApplicationTokenCommandOutput
            {
                Id = application.PublicId,
                Token = token.Token,
                ExpiresAt = token.ExpiresAt
            })
            .WithMessage(ApplicationTokenMessages.TokenIssued);
    }
}
```

Run: `dotnet build src/ArturRios.Heimdall.sln && dotnet test tests/Application/ArturRios.Heimdall.Command.Tests --no-build --filter "FullyQualifiedName~IssueApplicationToken"` — Expected: PASS (12).

- [ ] **Step 4: Register and add the endpoint**

In `Startup.AddDependencies`, after the UC-55 registration:

```csharp
        Builder.Services.AddScoped<IValidator<IssueApplicationTokenCommand>, IssueApplicationTokenCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<IssueApplicationTokenCommand, IssueApplicationTokenCommandOutput, IssueApplicationTokenCommandHandler>();
```

Create `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/ApplicationTokenController.cs`:

```csharp
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ArturRios.Heimdall.WebApi.Controllers;

/// <summary>
///     Authentication for applications (UC-56, UC-57, FR-AP-13…FR-AP-16): exchanging a credential
///     for an application token, and checking such a token live.
/// </summary>
/// <remarks>
///     Both actions are anonymous to <c>AuthenticationMiddleware</c>. The middleware knows only
///     person tokens — and refuses application tokens (TH-35) — so UC-56 is authenticated by its
///     secret and UC-57 validates the bearer token itself.
/// </remarks>
[Route("api/applications")]
public class ApplicationTokenController(CommandMediator commandMediator, QueryMediator queryMediator) : Controller
{
    /// <summary>
    ///     Exchanges an application's secret for a token valid for ten minutes by default (UC-56).
    ///     Every refusal is the same 401, so the endpoint reveals nothing about which applications,
    ///     scopes or credentials exist. Rate-limited per client address like the person sign-in.
    /// </summary>
    [HttpPost("token")]
    [AllowAnonymous]
    [EnableRateLimiting(Startup.AuthEndpointRateLimitPolicy)]
    public async Task<ActionResult<DataOutput<IssueApplicationTokenCommandOutput?>>> IssueToken(
        [FromBody] IssueApplicationTokenCommand command)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<IssueApplicationTokenCommand, IssueApplicationTokenCommandOutput>(command);

        return result.ToActionResult(statusMap: ApplicationTokenMessageMap.StatusCodes);
    }
}
```

If `AllowAnonymous` is ambiguous between `ArturRios.Util.WebApi.Security.Attributes` and `Microsoft.AspNetCore.Authorization`, keep the library's, as `AuthController` does. (`queryMediator` is used in Task 6.)

- [ ] **Step 5: Write the functional tests**

Create `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationTokenControllerIssueTests.cs`. Keep each test at **nine or fewer** calls to the endpoint (Notes for the implementer). No `Authorize(...)`: the endpoint is anonymous.

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/applications/token (UC-56): the main flow (a token for the
// application, under the application audience, LastUsedAt stamped, audited with no actor), a secret
// with surrounding whitespace, AF-56a (malformed request) and AF-56b — every refusal identical,
// including scope isolation (a credential of scope A cannot obtain a token naming scope B).
[Collection(nameof(FunctionalCollection))]
public class ApplicationTokenControllerIssueTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Route = "/api/applications/token";

    private Task<HttpResponse<DataOutput<IssueApplicationTokenCommandOutput?>>> ExchangeAsync(
        Guid applicationId, Guid scopeId, string secret) =>
        Gateway.PostAsync<DataOutput<IssueApplicationTokenCommandOutput?>>(Route,
            new IssueApplicationTokenCommand { ApplicationId = applicationId, ScopeId = scopeId, Secret = secret });

    [FunctionalFact]
    public async Task GivenALiveCredential_WhenExchanging_ThenOkWithAnApplicationToken()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, secret) = await SeedCredentialAsync(db, application);

        // When
        var response = await ExchangeAsync(application.PublicId, scope.PublicId, secret);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = new JsonWebToken(response.Body!.Data!.Token);
        Assert.Equal([PostgresFixture.ApplicationTokenAudience], token.Audiences);
        Assert.Equal(ApplicationTokenClaims.ApplicationKind, token.GetClaim(ApplicationTokenClaims.TokenKind).Value);
        Assert.Equal(application.PublicId.ToString(), token.GetClaim(ApplicationTokenClaims.Id).Value);
        Assert.Equal(scope.PublicId.ToString(), token.GetClaim(ApplicationTokenClaims.ScopeId).Value);
        Assert.Equal(credential.PublicId.ToString(), token.GetClaim(ApplicationTokenClaims.CredentialId).Value);
        Assert.NotNull(Assert.Single(await CredentialsOfAsync(db, application)).LastUsedAt);
    }

    [FunctionalFact]
    public async Task GivenAnExchange_WhenReadingTheAuditTrail_ThenTheApplicationIsTheTargetWithNoActor()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);

        // When
        await ExchangeAsync(application.PublicId, scope.PublicId, secret);

        // Then
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == nameof(IssueApplicationTokenCommand) && a.TargetId == application.PublicId);
        Assert.True(entry.Succeeded);
        Assert.Null(entry.ActorPersonId);
    }

    [FunctionalFact]
    public async Task GivenASecretWithATrailingNewline_WhenExchanging_ThenOk()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);

        // When
        var response = await ExchangeAsync(application.PublicId, scope.PublicId, secret + "\n");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenEveryKindOfRefusal_WhenExchanging_ThenEachIsTheSameUnauthorizedAnswer()
    {
        // Given — seven ways to be wrong, compared with the timestamps normalised
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);
        var (_, revoked) = await SeedCredentialAsync(db, application, revokedAt: DateTime.UtcNow.AddMinutes(-1));
        var (otherScope, _, otherApplication) = await SeedOwnedApplicationAsync(db);
        var (_, expired) = await SeedCredentialAsync(db, otherApplication, expiresAt: DateTime.UtcNow.AddMinutes(-1));
        var (_, otherSecret) = await SeedCredentialAsync(db, otherApplication);

        // When
        var responses = new[]
        {
            await ExchangeAsync(application.PublicId, scope.PublicId, "hds_" + new string('x', 43)),
            await ExchangeAsync(application.PublicId, scope.PublicId, revoked),
            await ExchangeAsync(otherApplication.PublicId, otherScope.PublicId, expired),
            await ExchangeAsync(application.PublicId, scope.PublicId, otherSecret),      // another application's
            await ExchangeAsync(Guid.NewGuid(), scope.PublicId, secret),                 // unknown application
            await ExchangeAsync(application.PublicId, otherScope.PublicId, secret)       // scope isolation
        };

        // Then
        Assert.All(responses, response =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal([ApplicationTokenMessages.CredentialsNotAccepted], response.Body!.Errors);
            Assert.Null(response.Body.Data);
        });
    }

    [FunctionalFact]
    public async Task GivenADeletedApplicationOrScope_WhenExchanging_ThenUnauthorized()
    {
        // Given — one application logically deleted, another whose scope is
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);
        var (deletedScope, _, inDeletedScope) = await SeedOwnedApplicationAsync(db);
        var (_, secretInDeletedScope) = await SeedCredentialAsync(db, inDeletedScope);
        await using (var context = db.CreateContext())
        {
            await context.Applications.Where(x => x.Id == application.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
            await context.Scopes.Where(x => x.Id == deletedScope.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
        }

        // When
        var first = await ExchangeAsync(application.PublicId, scope.PublicId, secret);
        var second = await ExchangeAsync(inDeletedScope.PublicId, deletedScope.PublicId, secretInDeletedScope);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenARefusal_WhenReadingTheAuditTrail_ThenItIsRecordedWithoutATarget()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var before = DateTime.UtcNow.AddSeconds(-1);

        // When
        await ExchangeAsync(application.PublicId, scope.PublicId, "hds_" + new string('y', 43));

        // Then — the canonical reason, never the submitted secret
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking()
            .Where(a => a.Action == nameof(IssueApplicationTokenCommand) && !a.Succeeded && a.CreatedAt >= before)
            .OrderByDescending(a => a.Id)
            .FirstAsync();
        Assert.Null(entry.TargetId);
        Assert.Equal(ApplicationTokenMessages.CredentialsNotAccepted, entry.FailureReason);
    }

    [FunctionalFact]
    public async Task GivenAMissingSecret_WhenExchanging_ThenBadRequest()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);

        // When
        var response = await ExchangeAsync(application.PublicId, scope.PublicId, string.Empty);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([ApplicationTokenMessages.SecretRequired], response.Body!.Errors);
    }
}
```

Two cases of the spec's list sit in the second refusal test rather than the first, so the first stays within nine calls. The functional collection runs tests one at a time, so the "refusal" audit query by time window does not see another test's rows; if the audit table's timestamp column is not `CreatedAt`, use its name.

- [ ] **Step 6: Run the tests, regenerate OpenAPI**

As Task 3 Step 11. Expected: PASS; the document gains `POST /api/applications/token` without a security requirement (it is `[AllowAnonymous]`).

- [ ] **Step 7: Commit**

Stage the task's files explicitly, plus `Startup.cs` and `docs/openapi/heimdall.json`. Subject: `feat: exchange application secrets for tokens`. Body: UC-56, the single 401, the rate limit, the audit entry with no actor; then the two trailer lines.

---

### Task 6: `GET /api/applications/self` — the live re-check (UC-57)

**Files:**
- Create: `src/Application/ArturRios.Heimdall.Query/Input/CheckApplicationTokenQuery.cs`, `Output/ApplicationTokenCheckOutput.cs`, `Handlers/CheckApplicationTokenQueryHandler.cs`
- Modify: `src/Application/ArturRios.Heimdall.Shared/Messages/ApplicationTokenMessageMap.cs`
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/ApplicationTokenController.cs`, `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`
- Test: `tests/Application/ArturRios.Heimdall.Query.Tests/CheckApplicationTokenQueryHandlerTests.cs`, `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationTokenControllerSelfTests.cs`

**Interfaces:**
- Consumes: `IApplicationTokenValidator`/`ApplicationTokenPrincipal` (Task 2), `ApplicationCredential.LiveAt`, `ApplicationTokenMessages.TokenAccepted`/`TokenNotAccepted` (Task 5), `ApplicationSeeds`.
- Produces: `CheckApplicationTokenQuery { Token }`; `ApplicationTokenCheckOutput { ApplicationId, Name, ScopeId, CredentialId, ExpiresAt }`; action `Self`.

- [ ] **Step 1: Write the failing handler tests**

`tests/Application/ArturRios.Heimdall.Query.Tests/CheckApplicationTokenQueryHandlerTests.cs`:

```csharp
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for CheckApplicationTokenQueryHandler (UC-57): a token the validator accepts is still
// refused unless its application and scope are live, the scope matches, and the issuing credential
// is live — so revocation, expiry and deletion take effect at the next re-check (AF-57a, one answer).
public class CheckApplicationTokenQueryHandlerTests
{
    private readonly AsyncFakeRepository<ApplicationCredential, long> _credentials = new();
    private readonly Mock<IApplicationTokenValidator> _validator = new();
    private readonly DateTime _expiresAt = DateTime.UtcNow.AddMinutes(10);

    private CheckApplicationTokenQueryHandler Handler() => new(_validator.Object, _credentials);

    private async Task<ApplicationCredential> SeedAsync(
        bool applicationDeleted = false, bool scopeDeleted = false, DateTime? expiresAt = null, DateTime? revokedAt = null)
    {
        var scope = new Scope { Id = 1, PublicId = Guid.NewGuid(), Name = "Acme", IsDeleted = scopeDeleted };
        var application = new Application
        {
            Id = 1, PublicId = Guid.NewGuid(), Name = "Billing Service", IsDeleted = applicationDeleted,
            ScopeId = scope.Id, Scope = scope
        };
        var credential = new ApplicationCredential
        {
            Id = 1, ApplicationId = application.Id, Application = application, SecretHash = "h",
            ExpiresAt = expiresAt, RevokedAt = revokedAt
        };
        await _credentials.CreateAsync(credential);
        return credential;
    }

    private void TokenSays(ApplicationCredential credential, Guid? scopeId = null) =>
        _validator.Setup(v => v.ValidateAsync("token")).ReturnsAsync(new ApplicationTokenPrincipal(
            credential.Application.PublicId, scopeId ?? credential.Application.Scope.PublicId,
            credential.PublicId, _expiresAt));

    [UnitFact]
    public async Task GivenAValidTokenOfALiveCredential_WhenChecking_ThenTheApplicationIsReturned()
    {
        // Given
        var credential = await SeedAsync();
        TokenSays(credential);

        // When
        var output = await Handler().HandleAsync(new CheckApplicationTokenQuery { Token = "token" });

        // Then
        Assert.True(output.Success);
        Assert.Equal(ApplicationTokenMessages.TokenAccepted, output.Messages.Single());
        Assert.Equal(credential.Application.PublicId, output.Data!.ApplicationId);
        Assert.Equal("Billing Service", output.Data.Name);
        Assert.Equal(credential.Application.Scope.PublicId, output.Data.ScopeId);
        Assert.Equal(credential.PublicId, output.Data.CredentialId);
        Assert.Equal(_expiresAt, output.Data.ExpiresAt);
    }

    [UnitFact]
    public async Task GivenATokenTheValidatorRefuses_WhenChecking_ThenNotAccepted()
    {
        // Given
        await SeedAsync();
        _validator.Setup(v => v.ValidateAsync(It.IsAny<string?>())).ReturnsAsync((ApplicationTokenPrincipal?)null);

        // When
        var output = await Handler().HandleAsync(new CheckApplicationTokenQuery { Token = "token" });

        // Then
        Assert.Equal([ApplicationTokenMessages.TokenNotAccepted], output.Errors);
    }

    [UnitTheory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("application-deleted")]
    [InlineData("scope-deleted")]
    [InlineData("other-scope")]
    public async Task GivenAValidTokenThatIsNoLongerBackedByLiveData_WhenChecking_ThenNotAccepted(string state)
    {
        // Given
        var credential = await SeedAsync(
            applicationDeleted: state == "application-deleted",
            scopeDeleted: state == "scope-deleted",
            expiresAt: state == "expired" ? DateTime.UtcNow.AddSeconds(-1) : null,
            revokedAt: state == "revoked" ? DateTime.UtcNow.AddSeconds(-1) : null);
        TokenSays(credential, scopeId: state == "other-scope" ? Guid.NewGuid() : null);

        // When
        var output = await Handler().HandleAsync(new CheckApplicationTokenQuery { Token = "token" });

        // Then
        Assert.Equal([ApplicationTokenMessages.TokenNotAccepted], output.Errors);
        Assert.Null(output.Data);
    }
}
```

Run: `dotnet build src/ArturRios.Heimdall.sln` — Expected: build FAILS.

- [ ] **Step 2: Write the query, output and handler**

`src/Application/ArturRios.Heimdall.Query/Input/CheckApplicationTokenQuery.cs`:

```csharp
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Checks an application token live (UC-57, FR-AP-15). <see cref="Token" /> is the bearer token
///     the controller read from the <c>Authorization</c> header, or <c>null</c> when there was none.
/// </summary>
public class CheckApplicationTokenQuery : BaseQuery
{
    public string? Token { get; set; }
}
```

`src/Application/ArturRios.Heimdall.Query/Output/ApplicationTokenCheckOutput.cs`:

```csharp
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>UC-57's answer: the application the token stands for, as it is now.</summary>
public class ApplicationTokenCheckOutput : QueryOutput
{
    public Guid ApplicationId { get; set; }

    /// <summary>The application's current name, which may differ from the token's <c>name</c> claim after a rename.</summary>
    public string Name { get; set; } = string.Empty;

    public Guid ScopeId { get; set; }

    public Guid CredentialId { get; set; }

    /// <summary>When the presented token expires.</summary>
    public DateTime ExpiresAt { get; set; }
}
```

`src/Application/ArturRios.Heimdall.Query/Handlers/CheckApplicationTokenQueryHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="CheckApplicationTokenQuery" /> (UC-57, FR-AP-15/16): validates the token
///     offline, then confirms against the database that its application and scope are live and
///     agree with it, and that the credential it was issued from is still live.
/// </summary>
/// <remarks>
///     This is the documented revocation policy: revoking a credential (UC-55), letting it expire, or
///     deleting the application or its scope takes effect here at once, for tokens already issued as
///     much as for new ones. A consumer re-checks before every sensitive release. Every refusal is
///     <see cref="ApplicationTokenMessages.TokenNotAccepted" />, with no detail.
/// </remarks>
public class CheckApplicationTokenQueryHandler(
    IApplicationTokenValidator validator,
    IAsyncReadOnlyRepository<ApplicationCredential, long> credentialReader)
    : IQueryHandlerAsync<CheckApplicationTokenQuery, ApplicationTokenCheckOutput>
{
    public async Task<DataOutput<ApplicationTokenCheckOutput?>> HandleAsync(
        CheckApplicationTokenQuery query, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<ApplicationTokenCheckOutput?>.New;

        var principal = await validator.ValidateAsync(query.Token);

        if (principal is null)
        {
            return output.WithError(ApplicationTokenMessages.TokenNotAccepted);
        }

        var now = DateTime.UtcNow;
        var check = await credentialReader.Query()
            .Where(ApplicationCredential.LiveAt(now))
            .Where(x => x.PublicId == principal.CredentialId &&
                        x.Application.PublicId == principal.ApplicationId &&
                        !x.Application.IsDeleted &&
                        x.Application.Scope.PublicId == principal.ScopeId &&
                        !x.Application.Scope.IsDeleted)
            .Select(x => new ApplicationTokenCheckOutput
            {
                ApplicationId = x.Application.PublicId,
                Name = x.Application.Name,
                ScopeId = x.Application.Scope.PublicId,
                CredentialId = x.PublicId,
                ExpiresAt = principal.ExpiresAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        return check is null
            ? output.WithError(ApplicationTokenMessages.TokenNotAccepted)
            : output.WithData(check).WithMessage(ApplicationTokenMessages.TokenAccepted);
    }
}
```

Run: `dotnet build src/ArturRios.Heimdall.sln && dotnet test tests/Application/ArturRios.Heimdall.Query.Tests --no-build --filter "FullyQualifiedName~CheckApplicationToken"` — Expected: PASS (7).

- [ ] **Step 3: Map, register, and add the endpoint**

In `ApplicationTokenMessageMap`, add:

```csharp
            // UC-57 main flow — the token stands.
            [ApplicationTokenMessages.TokenAccepted] = HttpStatusCodes.Ok,
            // AF-57a — any refusal: no or malformed token, wrong signature, issuer, audience or kind,
            // expired, or an application, scope or credential no longer live. One status, one message.
            [ApplicationTokenMessages.TokenNotAccepted] = HttpStatusCodes.Unauthorized
```

In `Startup.AddDependencies`, beside the application queries:

```csharp
        Builder.Services
            .AddScoped<IQueryHandlerAsync<CheckApplicationTokenQuery, ApplicationTokenCheckOutput>,
                CheckApplicationTokenQueryHandler>();
```

In `ApplicationTokenController` (add `using ArturRios.Heimdall.Query.Input;` and `using ArturRios.Heimdall.Query.Output;`):

```csharp
    /// <summary>
    ///     Checks an application token live (UC-57), presented as <c>Authorization: Bearer …</c>.
    ///     Answers the application, its scope and the credential when the token is valid and all
    ///     three are still live; otherwise one 401. Anonymous to the middleware, which accepts only
    ///     person tokens: the handler validates the token itself.
    /// </summary>
    [HttpGet("self")]
    [AllowAnonymous]
    public async Task<ActionResult<DataOutput<ApplicationTokenCheckOutput?>>> Self()
    {
        var query = new CheckApplicationTokenQuery { Token = BearerToken(Request) };

        var result = await queryMediator
            .ExecuteQueryAsync<CheckApplicationTokenQuery, ApplicationTokenCheckOutput>(query);

        return result.ToActionResult(statusMap: ApplicationTokenMessageMap.StatusCodes);
    }

    /// <summary>
    ///     The token of a <c>Bearer</c> <c>Authorization</c> header, or <c>null</c>. The scheme is
    ///     matched case-insensitively (RFC 9110 §11.1); any other scheme, or none, is no token.
    /// </summary>
    private static string? BearerToken(HttpRequest request)
    {
        const string scheme = "Bearer ";
        var header = request.Headers.Authorization.ToString();

        return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? header[scheme.Length..].Trim()
            : null;
    }
```

- [ ] **Step 4: Write the functional tests**

Create `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationTokenControllerSelfTests.cs`. Obtain tokens through the real `POST /api/applications/token` (helper below), so these tests prove the full flow the spec's §8 lists. Keep each test at nine or fewer token requests.

```csharp
using System.Net;
using System.Net.Http.Headers;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET /api/applications/self (UC-57) and the isolation between application and
// person tokens (TH-35): the full flow (issue a secret, exchange it, re-check), revocation, credential
// expiry and deletion taking effect on tokens already issued, a person token refused here, an
// application token refused on a person endpoint, and the header forms.
[Collection(nameof(FunctionalCollection))]
public class ApplicationTokenControllerSelfTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string SelfRoute = "/api/applications/self";

    private async Task<string> TokenForAsync(Scope scope, Domain.Entities.Application application, string secret)
    {
        var response = await Gateway.PostAsync<DataOutput<IssueApplicationTokenCommandOutput?>>(
            "/api/applications/token",
            new IssueApplicationTokenCommand { ApplicationId = application.PublicId, ScopeId = scope.PublicId, Secret = secret });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Body!.Data!.Token;
    }

    private Task<HttpResponse<DataOutput<ApplicationTokenCheckOutput?>>> SelfAsync(string token)
    {
        Authorize(token);
        return Gateway.GetAsync<DataOutput<ApplicationTokenCheckOutput?>>(SelfRoute);
    }

    [FunctionalFact]
    public async Task GivenAnIssuedSecret_WhenExchangedAndChecked_ThenTheApplicationIsReturned()
    {
        // Given — the secret comes from UC-53 itself, not from a seed
        var (scope, owner, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));
        var issued = await Gateway.PostAsync<DataOutput<IssueApplicationCredentialCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), new IssueApplicationCredentialCommand());
        var token = await TokenForAsync(scope, application, issued.Body!.Data!.Secret);

        // When
        var response = await SelfAsync(token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(application.PublicId, response.Body!.Data!.ApplicationId);
        Assert.Equal(scope.PublicId, response.Body.Data.ScopeId);
        Assert.Equal(issued.Body.Data.Id, response.Body.Data.CredentialId);
        Assert.Equal(application.Name, response.Body.Data.Name);
    }

    [FunctionalFact]
    public async Task GivenOneOfTwoCredentialsRevoked_WhenCheckingTokensOfBoth_ThenOnlyTheOtherStands()
    {
        // Given — tokens issued from both, then one credential revoked through UC-55
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (first, firstSecret) = await SeedCredentialAsync(db, application);
        var (_, secondSecret) = await SeedCredentialAsync(db, application);
        var firstToken = await TokenForAsync(scope, application, firstSecret);
        var secondToken = await TokenForAsync(scope, application, secondSecret);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var revoke = await Gateway.DeleteAsync<DataOutput<RevokeApplicationCredentialCommandOutput?>>(
            $"{Route(scope.PublicId, application.PublicId)}/{first.PublicId}");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        // When
        var revokedCheck = await SelfAsync(firstToken);
        var otherCheck = await SelfAsync(secondToken);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, revokedCheck.StatusCode);
        Assert.Equal([ApplicationTokenMessages.TokenNotAccepted], revokedCheck.Body!.Errors);
        Assert.Equal(HttpStatusCode.OK, otherCheck.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenItsCredentialExpiredAfterIssue_WhenCheckingSelf_ThenUnauthorized()
    {
        // Given — the token still has minutes left, the credential has none
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, secret) = await SeedCredentialAsync(db, application);
        var token = await TokenForAsync(scope, application, secret);
        await using (var context = db.CreateContext())
        {
            await context.ApplicationCredentials.Where(x => x.Id == credential.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        // When
        var response = await SelfAsync(token);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenTheApplicationThenItsScopeDeleted_WhenCheckingSelf_ThenUnauthorized()
    {
        // Given — two applications; one logically deleted, the other's scope logically deleted
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);
        var token = await TokenForAsync(scope, application, secret);
        var (otherScope, _, other) = await SeedOwnedApplicationAsync(db);
        var (_, otherSecret) = await SeedCredentialAsync(db, other);
        var otherToken = await TokenForAsync(otherScope, other, otherSecret);
        await using (var context = db.CreateContext())
        {
            await context.Applications.Where(x => x.Id == application.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
            await context.Scopes.Where(x => x.Id == otherScope.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
        }

        // When
        var deletedApplication = await SelfAsync(token);
        var deletedScope = await SelfAsync(otherToken);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, deletedApplication.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deletedScope.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAPersonToken_WhenCheckingSelf_ThenUnauthorized()
    {
        // Given — a valid System Admin token, signed with the same key
        var token = TestTokens.ForRole((int)Roles.SystemAdmin);

        // When
        var response = await SelfAsync(token);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([ApplicationTokenMessages.TokenNotAccepted], response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAnApplicationToken_WhenCallingAPersonEndpoint_ThenUnauthorized()
    {
        // Given — TH-35: the middleware checks no audience; the mapper's tokenKind refusal holds
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);
        var token = await TokenForAsync(scope, application, secret);
        Authorize(token);

        // When
        var response = await Gateway.Client.GetAsync(
            $"/api/persons/{PostgresFixture.StandInPersonIds[Roles.SystemAdmin]}");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenALowercaseBearerScheme_WhenCheckingSelf_ThenOk()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);
        var token = await TokenForAsync(scope, application, secret);
        using var request = new HttpRequestMessage(HttpMethod.Get, SelfRoute);
        request.Headers.TryAddWithoutValidation("Authorization", $"bearer {token}");

        // When
        var response = await Gateway.Client.SendAsync(request);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoAuthorizationHeader_WhenCheckingSelf_ThenUnauthorized()
    {
        // Given / When
        var response = await Gateway.GetAsync<DataOutput<ApplicationTokenCheckOutput?>>(SelfRoute);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([ApplicationTokenMessages.TokenNotAccepted], response.Body!.Errors);
    }
}
```

`Authorize(...)` sets the default header on `Gateway.Client`, so the lowercase-scheme test sends its own request on the same client without calling it. If `Authorize` is cumulative or `Gateway.Client` keeps a header between calls in a way that leaks into the "no header" test, give that test a fresh header state the way other tests in the suite clear it. `PostgresFixture.StandInPersonIds` is the fixture's public dictionary.

- [ ] **Step 5: Run every project, regenerate OpenAPI**

As Task 3 Step 11. Expected: PASS; the document gains `GET /api/applications/self`.

- [ ] **Step 6: Commit**

Stage explicitly, plus `Startup.cs` and `docs/openapi/heimdall.json`. Subject: `feat: re-check application tokens live`. Body: UC-57, revocation, expiry and deletion effective at the next check, the isolation test both ways; then the two trailer lines.

---

### Task 7: Specify and document UC-53 to UC-57

**Files:**
- Modify: `docs/requirements/Use Case Specification Document.md`
- Modify: `docs/requirements/System Requirements Document.md`
- Modify: `docs/requirements/Threat Model Document.md`
- Modify: `docs/requirements/Data Protection Document.md`
- Modify: `docs/requirements/Data Retention Schedule Document.md`
- Create: `docs/content/en/docs/flows/application-authentication.md`
- Modify: `docs/content/en/docs/api-reference.md`, `docs/content/en/docs/operations.md`, `docs/content/en/docs/getting-started.md`, `README.md`, `CHANGELOG.md`
- Modify: `api-client/http/applications.http`, `api-client/http/http-client.env.json`, `api-client/bruno/environments/*.bru`; Create: five `.bru` files in `api-client/bruno/Applications/`

**Interfaces:**
- Consumes: everything Tasks 1–6 built; the message texts of Decision 3; the alternative-flow letters below.
- Produces: documentation only.

Alternative-flow letters, used verbatim in the Use Case document and already cited by the message maps:

| UC | AF | Condition | Outcome |
| --- | --- | --- | --- |
| UC-53 | AF-53a | No live application with that id in the addressed, live scope | `404` `ApplicationNotFound` |
| UC-53 | AF-53b | A Scope Admin who does not own the application, or no longer owns its scope | `403` `NotAuthorizedToManageCredentials`; a `User` `403`, anonymous `401` |
| UC-53 | AF-53c | `expiresAt` not in the future | `400` `ExpiryNotInFuture` |
| UC-53 | AF-53d | Two live credentials already | `409` `CredentialLimitReached` |
| UC-54 | AF-54a / AF-54b | As AF-53a / AF-53b | `404` / `403` |
| UC-54 | AF-54c | No credentials | `200`, empty list |
| UC-55 | AF-55a / AF-55b | As AF-53a / AF-53b | `404` / `403` |
| UC-55 | AF-55c | No credential with that id on this application | `404` `CredentialNotFound` |
| UC-55 | AF-55d | Already revoked | `409` `CredentialAlreadyRevoked` |
| UC-56 | AF-56a | A field missing, or the secret over 100 characters | `400` with the field's message |
| UC-56 | AF-56b | Anything else wrong: unknown, revoked or expired secret; another application's; the application or scope deleted; the wrong scope | `401` `CredentialsNotAccepted`, always the same |
| UC-57 | AF-57a | No or malformed token; wrong signature, issuer, audience or kind; expired; application, scope or credential no longer live, or the scope not the token's | `401` `TokenNotAccepted`, always the same |

- [ ] **Step 1: Use Case Specification Document**

After UC-48's section (and its closing `---`), add UC-53 to UC-57 in UC-48's exact shape: the field table (ID, Name, Actors, Description, Preconditions, Postconditions), a `mermaid` `sequenceDiagram`, a numbered main flow, the alternative-flow table from above, and a short notes block where noted.

- **UC-53 Issue an Application Credential** — Actors: System Admin, Scope Admin. `POST /api/scopes/{scopeId}/applications/{id}/credentials`. Postcondition: a credential exists whose hash alone is stored; the secret was returned once; the issue is audited with the credential as target. Main flow:
  1. the actor asks for a credential, optionally with an expiry (FR-AP-10);
  2. the system validates the expiry (in the future, read as UTC without an offset);
  3. it resolves the application in the live scope;
  4. it checks the actor may manage it (FR-AP-12);
  5. it generates a secret (`hds_` plus 32 random bytes) and stores only its SHA-256, under a lock on the application's row, provided fewer than two live credentials exist (FR-AP-11);
  6. it answers `201` with the secret.

  Notes: why two (rotation without downtime); why the secret is never shown again.
- **UC-54 List an Application's Credentials** — `GET …/credentials`. Newest first, each with `id`, `createdAt`, `expiresAt`, `revokedAt`, `lastUsedAt`, `live`; never the secret or its hash; not paginated (at most two live, spent ones purged).
- **UC-55 Revoke an Application Credential** — `DELETE …/credentials/{credentialId}`. Postcondition: `RevokedAt` set; UC-56 refuses the secret, and UC-57 refuses every token already issued from it. An expired credential may still be revoked.
- **UC-56 Obtain an Application Token** — Actor: Application (anonymous). `POST /api/applications/token`, body `applicationId`, `scopeId`, `secret`. Main flow:
  1. the application sends the three fields;
  2. the system validates their shape;
  3. it looks the credential up by the hash of the trimmed secret among live credentials;
  4. it confirms the credential belongs to that application, the application to that scope, and both are live;
  5. it issues a token signed like person tokens but under `HEIMDALL_APPLICATION_TOKEN_AUDIENCE`, valid for `HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS` (default 600), with the claims `tokenKind`, `id`, `scopeId`, `credentialId` and `name` and no role (FR-AP-13);
  6. it stamps `LastUsedAt`, audits the issue (no actor; the application as target), and answers `200` with `token` and `expiresAt`.

  Notes: the one refusal, as UC-11; the rate limit shared with person sign-in (TH-32's trusted caller key applies).
- **UC-57 Check an Application Token** — Actor: a consumer service, holding the application's token. `GET /api/applications/self` with `Authorization: Bearer`. Main flow:
  1. validate the token (signature against the current and previous keys, issuer, application audience, lifetime with zero skew, `tokenKind`);
  2. confirm the credential is live and the application and scope are live and match the token (FR-AP-15);
  3. answer `200` with `applicationId`, `name`, `scopeId`, `credentialId` and `expiresAt`.

  Notes — **revocation policy** (FR-AP-16): revoking, expiring or deleting takes effect at the next check, for tokens already issued; consumers re-check before every sensitive release.

Add to the traceability table, after UC-48's row:

```markdown
| UC-53: Issue an Application Credential | FR-AP-10, FR-AP-11, FR-AP-12, NFR-09 |
| UC-54: List an Application's Credentials | FR-AP-12 |
| UC-55: Revoke an Application Credential | FR-AP-12, FR-AP-16, NFR-09 |
| UC-56: Obtain an Application Token | FR-AP-13, FR-AP-14, NFR-09 |
| UC-57: Check an Application Token | FR-AP-15, FR-AP-16 |
```

UC-49 to UC-52 arrive with #182 and #183. Whichever branch merges later resolves the conflict by keeping every section in UC order and every traceability row.

- [ ] **Step 2: System Requirements Document**

In §3.7 Application Management, after FR-AP-09:

```markdown
| FR-AP-10 | The system shall let a System Admin, or a Scope Admin who owns an application and its scope, issue the application a credential: a random secret shown once and stored only as its SHA-256, optionally with an expiry | High |
| FR-AP-11 | An application shall hold at most **two** live credentials (not revoked, not expired), so a new secret can be deployed before the old one is revoked | High |
| FR-AP-12 | The same actors shall be able to list an application's credentials — never the secret or its hash — and revoke any one of them | High |
| FR-AP-13 | The system shall exchange a live credential for an application token: HMAC-SHA256 with the person tokens' keys, a distinct audience, a lifetime of 10 minutes by default, and the claims `tokenKind = application`, `id`, `scopeId`, `credentialId` and `name`, with no role | High |
| FR-AP-14 | Every refused exchange shall answer the same `401` with the same message, revealing nothing about which applications, scopes or credentials exist | High |
| FR-AP-15 | The system shall let a consumer check an application token live, confirming the application, its scope and the issuing credential are still live | High |
| FR-AP-16 | Revoking or expiring a credential, or deleting the application or its scope, shall make every token issued from it fail the live check at once | High |
```

In §5.3 Application Endpoints, append:

```markdown
| POST | `/api/scopes/{scopeId}/applications/{id}/credentials` | Issue a credential; the secret is returned once | SystemAdmin / ScopeAdmin (owner of the application) |
| GET | `/api/scopes/{scopeId}/applications/{id}/credentials` | List the application's credentials | SystemAdmin / ScopeAdmin (owner of the application) |
| DELETE | `/api/scopes/{scopeId}/applications/{id}/credentials/{credentialId}` | Revoke a credential | SystemAdmin / ScopeAdmin (owner of the application) |
| POST | `/api/applications/token` | Exchange a credential for an application token | No (the secret authenticates) |
| GET | `/api/applications/self` | Check an application token live | Application token (validated by the endpoint) |
```

In §7's authorization table, after "Delete Application (hard)":

```markdown
| Issue/List/Revoke Application Credentials | ✅ | ✅ (owned application) | ❌ | ❌ |
| Obtain an Application Token | — | — | — | ✅ (with a live secret) |
| Check an Application Token | — | — | — | ✅ (with an application token) |
```

In §9, change "Application CRUD | FR-AP-01 through FR-AP-09" to "Application CRUD and authentication | FR-AP-01 through FR-AP-16". If an NFR lists the environment variables, add the two new ones there too.

- [ ] **Step 3: Threat Model Document**

After TH-32 in §4's table, in its five-column shape (threat, mitigation, verified by, residual):

```markdown
| TH-33 | A stolen application secret used to obtain tokens as that application (UC-56) | Shown once and stored only as SHA-256, so neither the database nor the logs hold it; `hds_`-prefixed so a scanner recognises a leak; revocable one at a time, with a second live slot so rotation needs no downtime (FR-AP-11); optional expiry; every exchange audited with the application as target and `LastUsedAt` stamped; `AuthAnonymous` rate limit; one uniform refusal (FR-AP-14). **Residual:** until revoked, the holder authenticates as the application | Test | Medium |
| TH-34 | A leaked application token replayed (a log, a proxy) | Ten-minute lifetime by default; UC-57's live check refuses it as soon as its credential is revoked or expires or its application or scope is deleted (FR-AP-16), and consumers re-check before every sensitive release | Test | Low |
| TH-35 | Token confusion: an application token taken for a person, or a person token for an application | A distinct, required audience (start-up refuses a missing or equal one), so a consumer pinning the person audience refuses application tokens. Inside Heimdall, which checks no audience on person routes, `IdentityUserMapper` refuses any token carrying `tokenKind`, and an application token has no role. UC-57 accepts only the application audience with `tokenKind = application`, so person, Google User, challenge and subject-rights-only tokens all fail it | Test | Low |
```

Add a findings-register row (§11) for TH-35 in its style: "Finding: Heimdall's middleware validates signature and lifetime only, not the audience (`ArturRios.Jwt` `IsTokenValidAsync`). Response: the mapper refuses `tokenKind`; a functional test pins an application token's `401` on a person endpoint."

- [ ] **Step 4: Data Protection Document and Data Retention Schedule**

Data Protection Document, beneath the paragraph "`APPLICATION` rows are out of scope…", add:

> `APPLICATION_CREDENTIAL` rows (UC-53) are out of scope for the same reason. Each holds an application reference, the SHA-256 of a random secret, and four instants (issued, expiring, revoked, last used). None of them relates to a person, and the audit entries of credential operations name the acting administrator as every audit entry does (§5.1 `AUDIT_LOG`).

Data Retention Schedule, the "Single-use tokens" row of the summary table: add `APPLICATION_CREDENTIAL` (revoked or expired) to its tables column. In §5, add one paragraph: a revoked or expired credential can never be accepted again, so it is kept for the same grace as a spent token and then purged by the same pass. A live credential is kept for as long as it is live, since it is in use.

- [ ] **Step 5: Consumer guide**

Create `docs/content/en/docs/flows/application-authentication.md`. Use the front matter shape of `login.md`, after the last flow page (`audit-logging.md` is 70):

```
+++
title = 'Application authentication'
linkTitle = 'Application authentication'
weight = 80
description = 'UC-53 to UC-57 — credentials for a registered application, the token they buy, and the live re-check consumers make.'
+++
```

Sections, each written out in full prose and examples. No placeholders survive into the page.

1. **Who this is for:** a service (Cerberus is the first) that must authenticate a registered application, not a person.
2. **Provisioning and rotation:** issue with `curl`, showing the `201` body, and that the secret is shown once. Store it in the consumer's secret store. Rotate by issuing a second, deploying it, then revoking the first. Note the two-live limit and the `409`.
3. **Obtaining a token:** a `curl` example of `POST /api/applications/token` and its `200` body. Request a new token before `expiresAt`, and never per request. The single `401` and what to check when it appears: right application, right scope, live credential, no stray whitespace beyond a trailing newline.
4. **The token:** a table of the claims (`iss`, `aud`, `exp`/`iat`/`nbf`, `tokenKind`, `id`, `scopeId`, `credentialId`, `name`). The absence of any role.
5. **Validating offline:** HS256; the issuer; **the application audience** (`HEIMDALL_APPLICATION_TOKEN_AUDIENCE`); both signing keys during a rotation; zero clock skew; `tokenKind == "application"`. A consumer that validates person tokens must pin the person audience, or it will accept application tokens as persons.
6. **Checking live:** `GET /api/applications/self` with the bearer token, its `200` body and its single `401`.
7. **Revocation policy:** revoke, expiry, or deletion takes effect at the next live check. Re-check before every sensitive release; the offline check alone accepts a revoked token for up to its remaining lifetime.
8. **Status codes:** one table covering every endpoint, from the alternative-flow table above.

Link it from `docs/content/en/docs/flows/_index.md` if that page lists the flows.

- [ ] **Step 6: API reference, operations, getting started, README and changelog**

- `docs/content/en/docs/api-reference.md`: add the five endpoints wherever the application endpoints are listed, in that page's format, and link the consumer guide.
- `docs/content/en/docs/operations.md`, the environment table near `HEIMDALL_AUTH_TOKEN_ISSUER`:
  ```markdown
  | `HEIMDALL_APPLICATION_TOKEN_AUDIENCE` | ✅ | — (must differ from `HEIMDALL_AUTH_TOKEN_AUDIENCE`) |
  | `HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS` | | `600` |
  ```
  Match the table's real columns. `docs/requirements/Operations & Infrastructure Document.md` lists no token variables today; if it does by the time you get here, add the two there as well. Also add a sentence to the signing-secret rotation section: application tokens are signed with the same keys, so the same rotation covers them.
- `docs/content/en/docs/getting-started.md` and `README.md` environment tables: the same two rows in their format. README's backlog: an "Application authentication" table (or rows under Application Management, whichever the README uses) with UC-53 to UC-57 marked ✅ and linked to [#178](https://github.com/artur-rios/heimdall-api/issues/178). Update any "N operations/endpoints" count that this change makes stale.
- `CHANGELOG.md` `## [Unreleased]` `### Added`: one entry naming UC-53 to UC-57, the five endpoints, the new table (migration `AddApplicationCredential`), and the two variables. Add an upgrade note under `### Changed` or a `**Upgrading:**` line saying `HEIMDALL_APPLICATION_TOKEN_AUDIENCE` is now **required** and must differ from `HEIMDALL_AUTH_TOKEN_AUDIENCE`. `docker-compose.yml` defaults it to `heimdall-applications`.

- [ ] **Step 7: API clients**

`api-client/http/applications.http`, after the application requests:

```http
### Issue a credential (UC-53) — System Admin or the Scope Admin who owns the application.
# The secret is in this response only. Store it now.
POST {{host}}/api/scopes/{{scopeId}}/applications/{{applicationId}}/credentials
Authorization: Bearer {{token}}
Content-Type: application/json

{
  "expiresAt": "2030-01-01T00:00:00Z"
}

> {%
    if (response.body.data && response.body.data.id) {
        client.global.set("credentialId", response.body.data.id);
        client.global.set("applicationSecret", response.body.data.secret);
    }
%}

### List the application's credentials (UC-54).
GET {{host}}/api/scopes/{{scopeId}}/applications/{{applicationId}}/credentials
Authorization: Bearer {{token}}

### Obtain an application token (UC-56) — anonymous; the secret authenticates.
POST {{host}}/api/applications/token
Content-Type: application/json

{
  "applicationId": "{{applicationId}}",
  "scopeId": "{{scopeId}}",
  "secret": "{{applicationSecret}}"
}

> {%
    if (response.body.data && response.body.data.token) {
        client.global.set("applicationToken", response.body.data.token);
    }
%}

### Check the application token live (UC-57).
GET {{host}}/api/applications/self
Authorization: Bearer {{applicationToken}}

### Revoke the credential (UC-55). The token above then fails the live check.
DELETE {{host}}/api/scopes/{{scopeId}}/applications/{{applicationId}}/credentials/{{credentialId}}
Authorization: Bearer {{token}}
```

Add `credentialId`, `applicationSecret` and `applicationToken` (empty strings) to every environment in `api-client/http/http-client.env.json` and to every `api-client/bruno/environments/*.bru`.

Create the five Bruno requests in `api-client/bruno/Applications/`, following `Update an application.bru`:

| File | `seq` | Method and URL | Auth | Body | `docs` |
| --- | --- | --- | --- | --- | --- |
| `Issue a credential.bru` | 7 | `post` `{{host}}/api/scopes/{{scopeId}}/applications/{{applicationId}}/credentials` | `inherit` | `{ "expiresAt": "2030-01-01T00:00:00Z" }` | "UC-53. System Admin, or the Scope Admin who owns the application. The secret is in this response only." Plus a `script:post-response` setting `credentialId` and `applicationSecret` with `bru.setVar`, if other requests in the collection use post-response scripts; otherwise say in `docs` to copy them. |
| `List credentials.bru` | 8 | `get`, same URL | `inherit` | — | "UC-54." |
| `Obtain an application token.bru` | 9 | `post` `{{host}}/api/applications/token` | `none` | the three fields from `{{applicationId}}`, `{{scopeId}}`, `{{applicationSecret}}` | "UC-56. Anonymous. Every refusal is the same 401." |
| `Check an application token.bru` | 10 | `get` `{{host}}/api/applications/self` | `bearer` with `{{applicationToken}}` | — | "UC-57. Re-check before every sensitive release." |
| `Revoke a credential.bru` | 11 | `delete` `{{host}}/api/scopes/{{scopeId}}/applications/{{applicationId}}/credentials/{{credentialId}}` | `inherit` | — | "UC-55." |

If `api-client/README.md` lists the requests or counts them, update it too.

- [ ] **Step 8: Check the documents build and the gates pass**

Run:
```bash
python3 scripts/openapi.py --check
python3 scripts/vulnerabilities.py
```
If the repository has a docs link or markdown check (see `.github/workflows/build-docs.yml` for what CI runs), run it locally where possible. Expected: PASS.

- [ ] **Step 9: Commit**

Stage every document and client file explicitly. Subject: `docs: specify application authentication`. Body: UC-53 to UC-57, FR-AP-10 to FR-AP-16, TH-33 to TH-35, the consumer guide, the clients; then the two trailer lines.

---

## Verification

Run once all seven tasks are committed, in the worktree:

- [ ] `dotnet build src/ArturRios.Heimdall.sln`: no warnings beyond the ones on `origin/develop`.
- [ ] Each test project, one at a time:
  - `tests/Domain/ArturRios.Heimdall.Domain.Tests`
  - `tests/Application/ArturRios.Heimdall.Shared.Tests`
  - `tests/Application/ArturRios.Heimdall.Query.Tests`
  - `tests/Application/ArturRios.Heimdall.Command.Tests`
  - `tests/Infrastructure/ArturRios.Heimdall.Data.Tests`
  - `tests/Presentation/ArturRios.Heimdall.WebApi.Tests`

  Record each count.
- [ ] Coverage: the per-project coverage runs the repository uses (see `scripts/coverage.py --help`), then `python3 scripts/coverage.py --report-only`. Coverage must not fall below `origin/develop`'s.
- [ ] `python3 scripts/openapi.py --check`.
- [ ] `python3 scripts/vulnerabilities.py`.
- [ ] `dotnet ef migrations has-pending-model-changes` (with the dummy connection string), which must report no changes.
- [ ] Search the diff for a leaked secret path: `git diff origin/develop -- src | grep -n -i 'secret\|token' | grep -i 'log'`. Every hit must be a log line that writes neither value.
- [ ] Re-read the spec's §8 list and tick each item against a named test.
