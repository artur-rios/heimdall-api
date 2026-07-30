# UC-11: Login — Design

## Summary

Implement UC-11 (Login, FR-AU-01…FR-AU-07): authenticate a person by email and password and return a
signed token, through one endpoint:

| Method | Endpoint | Actors |
| --- | --- | --- |
| POST | `/api/auth/login` | Anonymous (no authentication required) |

A `User` supplies `scopeId` as well, since their email is only unique within their scope (FR-AU-01);
a `ScopeAdmin` or `SystemAdmin` supplies email and password only (FR-AU-02).

UC-11 also **completes FR-AU-04 and NFR-15 for the whole API**, which is why it was blocked: the token
must carry `PublicId` GUIDs and never an internal `bigint`. `ArturRios.Util.WebApi` 3.0.0 makes that
expressible, and upgrading to it is a compile-forced part of this use case (see Decision 1).

## What 3.0.0 changed

| 2.1.0 | 3.0.0 |
| --- | --- |
| `AuthenticatedUser(int Id, int Role)`, sealed record | `IAuthenticatedUser { Guid Id; int RoleId; }` — apps implement it on their own type |
| `AuthenticatedUserFactory.FromToken` (fixed `id`/`role` claims) | `IAuthenticatedUserMapper` — one implementation owns `ToClaims` **and** `FromClaims`, so issuing and validating cannot drift |
| `AddTokenAuthentication(...)` | `AddTokenAuthentication<TMapper>(...)` registers the app's mapper |
| `(AuthenticatedUser)HttpContext.Items["User"]!` | `HttpContext.GetUser<TUser>()` |
| `TokenClaimKeys.Role` | `TokenClaimKeys.RoleId` |
| `IAuthenticationProvider.GetAuthenticatedUserById(int)` | `…ById(Guid)` |

`JwtValidationMode.ClaimsOnly` remains the default, so nothing added here costs a database read per
request.

## Decisions

1. **Upgrade `ArturRios.Util.WebApi` to 3.0.0 inside UC-11, and carry the actor refactor with it.**
   `AuthenticatedUser.Id` becoming a `Guid` breaks `PersonController.ApplyActor` at compile time, and
   `IActorScoped.ActingPersonId` is a `long` compared against `person.Id` in five handlers. The
   upgrade cannot land without the refactor, and UC-11 cannot land without the upgrade, so splitting
   them would produce a branch that does not build. Scope is called out at Gate 1.

2. **`ActingPersonId` becomes the acting person's `PublicId` (`Guid`).** With `ClaimsOnly` the token
   is the only source of caller identity, and it may not carry an internal id. The alternative —
   `Revalidate` mode plus an `IAuthenticationProvider` that re-reads the person to recover the
   internal id — buys a smaller diff at the price of a database lookup on every authenticated
   request, and leaves internal ids in the request pipeline. The refactor is mechanical: a type
   change on eight command/query inputs, and four `== person.Id` comparisons becoming
   `== person.PublicId`.

3. **Token claims are `PublicId`s only** (FR-AU-04, NFR-15):

   | Claim | Value | Roles |
   | --- | --- | --- |
   | `id` (`TokenClaimKeys.Id`) | the person's `PublicId` | all |
   | `roleId` (`TokenClaimKeys.RoleId`) | the role value | all |
   | `scopeId` | the scope's `PublicId` | `User` only |
   | `ownedScopeIds` | comma-separated scope `PublicId`s | `ScopeAdmin` only |

   A `SystemAdmin` carries no scope claim. No internal `bigint` appears anywhere in the token.

4. **One generic `InvalidCredentials` message, `401`, for AF-11a…AF-11e.** The five conditions —
   unknown person, wrong password, deleted person, deleted scope, all owned scopes deleted — are
   indistinguishable to the caller, so a login response cannot be used to probe which emails exist,
   which are deleted, or which scopes are gone. UC-12 already states this posture explicitly for
   password recovery ("does not reveal whether the email exists"); UC-11 gets the same treatment.

5. **Checks run in the specification's order** (locate → hash and compare → `IsDeleted` → scope
   state), even though Decision 4 makes all five outcomes identical. Following the written order keeps
   the handler readable against the spec, and the order has no observable effect on the response.

6. **The lookup does not filter `IsDeleted`.** AF-11c exists precisely to reject a logically deleted
   person, so the person must be found first and rejected at step 4 — the same shape UC-09 and UC-10
   use.

7. **A validator, and a new AF-11f (`400`).** NFR-10 requires all inputs to be validated, and every
   other write flow in the project has one. `LoginCommandValidator` requires an email that is present
   and well-formed, and a password that is present. It deliberately does **not** impose the
   8-character minimum that `CreateAdminCommandValidator` applies at creation time — a login attempt
   with a short password is a failed login (401), not a malformed request. Recorded as AF-11f, in the
   same spirit as UC-10's AF-10c.

8. **The token issuer is an application-layer abstraction with a presentation-layer implementation.**
   `IAuthTokenIssuer` lives in `Command/Services`; `JwtAuthTokenIssuer` lives in `WebApi/Security`.
   This keeps the `ArturRios.Util.WebApi` package out of the Application layer while still letting the
   mapper be the single owner of the claim vocabulary — the same split
   `IEmailVerificationSender`/`LoggingEmailVerificationSender` already uses.

9. **`AuthController` is new**, rather than another action on `PersonController`. SRD §5.4 groups five
   endpoints under `/api/auth` (UC-11…UC-15); UC-11 opens that controller and UC-12–UC-15 fill it.

10. **`TestTokens` keeps minting tokens directly, but through the production mapper.** Rewriting the
    functional suite to log in for real would need every test to seed a person with a known password.
    Instead `TestTokens` calls the same `IdentityUserMapper` the API validates with, so a claim-shape
    change can never leave the tests passing against a stale token format.

## Routing

New `AuthController`:

- `[HttpPost("auth/login")]` with `[AllowAnonymous]` — SRD §5.4 lists the endpoint as requiring no
  authentication, and the authorization matrix (§7) grants Login to Anonymous. Without the attribute
  `AuthenticationMiddleware` rejects the request with 401 before it reaches the action.

The action dispatches through `CommandMediator` and returns
`ResponseResolver.Resolve(result, statusMap: AuthMessageMap.StatusCodes)`.

## Command, output, and identity

`LoginCommand : BaseCommand`

| Field | Notes |
| --- | --- |
| `Email` (string) | Required |
| `Password` (string) | Required |
| `ScopeId` (Guid?) | The scope's `PublicId`. Present selects the `User` lookup (FR-AU-01); absent selects the admin lookup (FR-AU-02) |

`LoginCommandOutput : CommandOutput`

| Field | Notes |
| --- | --- |
| `Token` (string) | The signed JWT |
| `ExpiresAt` (DateTime) | UTC expiry, from `JwtConfiguration.ExpirationInSeconds` |

`AuthTokenSubject` (Command/Services) — `Guid PersonId`, `int RoleId`, `Guid? ScopeId`,
`IReadOnlyCollection<Guid> OwnedScopeIds`. The application-layer description of who the token is for,
free of any web/JWT type.

`IdentityUser` (WebApi/Security) — the app's `IAuthenticatedUser`: `Guid Id`, `int RoleId`,
`Guid? ScopeId`, `IReadOnlyCollection<Guid> OwnedScopeIds`.

## Handler

`LoginCommandHandler` returns `DataOutput<LoginCommandOutput?>` and never throws.

1. **Validate** → AF-11f, 400.
2. **Locate the person** (UC-11 step 2). With `ScopeId`: a person holding the `User` role whose
   `ScopeMembership.Scope.PublicId` matches and whose email matches case-insensitively. Without: a
   person holding `ScopeAdmin` or `SystemAdmin`, email matched case-insensitively system-wide. Miss →
   **AF-11a**.
3. **Compare the password** with `Hash.TextMatches(command.Password, person.PasswordHash, person.Salt)`
   → false is **AF-11b**.
4. **`person.IsDeleted`** → **AF-11c** (FR-AU-05).
5. **Scope state.** A `User` whose scope `IsDeleted` → **AF-11d** (FR-AU-06). A `ScopeAdmin` with no
   owned scope where `!IsDeleted` → **AF-11e** (FR-AU-07). A `SystemAdmin` has no scope to check.
6. **Issue the token** from an `AuthTokenSubject` carrying the person's `PublicId`, role, and the
   scope claims their role calls for → 200 with `LoginSuccessful`.

Dependencies: `IValidator<LoginCommand>`, `IAsyncReadOnlyRepository<Person>`, `IAuthTokenIssuer`.

## Messages and status map

New `AuthMessages` / `AuthMessageMap`, mirroring the person and scope vocabularies:

| Message | Status | Flow |
| --- | --- | --- |
| `LoginSuccessful` | 200 OK | main flow |
| `InvalidCredentials` | 401 Unauthorized | AF-11a, AF-11b, AF-11c, AF-11d, AF-11e (Decision 4) |
| `EmailRequired`, `EmailInvalid`, `PasswordRequired` | 400 Bad Request | AF-11f |

## The actor refactor

Forced by Decision 1, mechanical throughout:

| File | Change |
| --- | --- |
| `Shared/Security/IActorScoped.cs` | `long ActingPersonId` → `Guid` |
| 8 command/query inputs | same type change |
| `Shared/Services/IScopeOwnershipChecker.cs` + impl | `long actingPersonId` → `Guid`; `person.Id ==` → `person.PublicId ==` |
| `DeletePersonCommandHandler`, `HardDeletePersonCommandHandler`, `UpdatePersonCommandHandler` | `command.ActingPersonId == person.Id` → `== person.PublicId` |
| `GetPersonByIdQueryHandler` | same comparison, plus `PublicId` added to `PersonProjection` and the owned-scope query keyed on `PublicId` |
| `PersonController.ApplyActor` | `HttpContext.GetUser<IdentityUser>()` |

No behaviour changes — every authorization rule decides the same way, on a different key.

## Dependency injection

- `AddTokenAuthentication<IdentityUserMapper>(...)`, options unchanged (`Header`, JWT on, Google off,
  `ClaimsOnly`).
- `IAuthTokenIssuer` → `JwtAuthTokenIssuer` (scoped).
- `IValidator<LoginCommand>` → `LoginCommandValidator`, and
  `ICommandHandlerAsync<LoginCommand, LoginCommandOutput>` → `LoginCommandHandler`.

## Components

| Layer | File | New/Edit |
| --- | --- | --- |
| Command / Input | `LoginCommand.cs`, `Validation/LoginCommandValidator.cs` | new |
| Command / Handlers | `LoginCommandHandler.cs` | new |
| Command / Output | `LoginCommandOutput.cs` | new |
| Command / Services | `IAuthTokenIssuer.cs`, `AuthTokenSubject.cs`, `AuthToken.cs` | new |
| Shared / Messages | `AuthMessages.cs`, `AuthMessageMap.cs` | new |
| Shared / Security | `IActorScoped.cs` | edit |
| Shared / Services | `IScopeOwnershipChecker.cs`, `ScopeOwnershipChecker.cs` | edit |
| Command, Query | 8 inputs, 5 handlers | edit (actor refactor) |
| Presentation | `Controllers/AuthController.cs`, `Security/IdentityUser.cs`, `Security/IdentityUserMapper.cs`, `Security/JwtAuthTokenIssuer.cs` | new |
| Presentation | `Controllers/PersonController.cs`, `Startup.cs`, `.csproj` | edit |
| Docs | `Use Case Specification Document.md`, `README.md` | edit |

## Documentation update

UC-11's specification gains, in the same change (as UC-07…UC-10 did):

| ID | Condition | Outcome |
| --- | --- | --- |
| AF-11f | Email missing/malformed, or password missing | 400 Bad Request |

Plus the explicit endpoint (`POST /api/auth/login`), and a note recording Decision 4 — that AF-11a
through AF-11e deliberately share one response so the endpoint cannot be used to enumerate accounts.

## Testing (Testing Specification §6–§7)

Note the project's own sequencing: Development Workflow §4 puts implementation at Step 3 and the
tests at Step 5, behind the **Testing** status. Tests are therefore written after Gate 2, not
test-first — except for the existing tests the actor refactor breaks at compile time, which are
repaired as part of the refactor because the solution cannot build otherwise.

**Unit — `Command.Tests`**, `LoginCommandHandlerTests` + `LoginCommandValidatorTests`:

- main flow, `User` with a valid scope → token issued carrying person + scope `PublicId`s;
- main flow, `ScopeAdmin` owning two live scopes → token carries both owned scope `PublicId`s;
- main flow, `SystemAdmin` → token carries no scope claim;
- main flow, email matched case-insensitively;
- AF-11a: unknown email; and an email that exists only in another scope;
- AF-11a: a `User`'s email submitted without a `scopeId` (the admin lookup must not find them);
- AF-11b: wrong password;
- AF-11c: logically deleted person;
- AF-11d: `User` whose scope is logically deleted;
- AF-11e: `ScopeAdmin` whose every owned scope is logically deleted;
- AF-11e boundary: one of two owned scopes still live → succeeds;
- AF-11f: missing email, malformed email, missing password.

Each AF asserts `InvalidCredentials` and that no token was issued.

**Unit — `WebApi.Tests`** (`[UnitFact]`), `IdentityUserMapperTests`: round-trip `ToClaims` →
`FromClaims` for each of the three role shapes, and `FromClaims` returning `null` for absent,
malformed, and non-GUID claims. This is the one place claim drift could hide, and it is pure logic.

**Functional — `WebApi.Tests`**, `AuthControllerLoginTests`, Testcontainers PostgreSQL:

- 200 for a seeded `User` with the right `scopeId`, asserting the decoded token's claims;
- 200 for a `ScopeAdmin`, asserting `ownedScopeIds`;
- 200 for the seeded master `SystemAdmin`, asserting no scope claim;
- **round trip**: log in, then call an authenticated endpoint with the returned token and get 200 —
  proving issuing and validating agree end to end;
- 401 for AF-11a…AF-11e, each seeded to hit exactly one condition;
- 400 for AF-11f;
- the endpoint answers without a bearer token (`[AllowAnonymous]`).

**Regression:** the full existing suite — every unit and functional test written for UC-01…UC-10 —
must stay green after the actor refactor. That is the real gate on Decision 2.

## Out of scope / non-goals

- No refresh tokens, no logout, no token revocation. NFR-03 asks only for signed tokens with a
  configurable expiry, which `JwtConfiguration` already provides.
- No password recovery or email verification flow — UC-12…UC-15 own `/api/auth`'s other endpoints.
- No Google sign-in; `EnableGoogle` stays `false` until UC-25.
- No rate limiting or lockout on repeated failures. Not in UC-11, not in the requirements.
- No schema change and no migration.
