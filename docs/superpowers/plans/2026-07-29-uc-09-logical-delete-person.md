# UC-09 Logical Delete Person Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement UC-09 (Logical Delete Person) — one `DELETE /api/persons/{id}` endpoint that sets `IsDeleted = true` on a person for the actors the use case permits, idempotently, without stripping any scope of its last owner.

**Architecture:** CQRS write flow mirroring UC-04 (Logical Delete Scope) for its idempotent lookup and UC-08 (Update Person) for its per-actor authorization. One command, handler, and output; the endpoint joins the existing `PersonController`. The handler returns `DataOutput<T>` and never throws. Actor identity arrives through the `IActorScoped` plumbing UC-07 moved into `Shared`.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core (PostgreSQL), FluentValidation (not used here — no validator), ArturRios.Mediator / .Output / .Data.Relational.Core / .Util.WebApi; xUnit + Moq + Bogus + Testcontainers for tests.

## Global Constraints

- **Design of record:** `docs/superpowers/specs/2026-07-29-uc-09-logical-delete-person-design.md`. Every decision below traces to it.
- **No schema change / no EF migration** — `person.is_deleted` already exists from `InitialCreate`.
- **Identifiers:** routes, inputs and outputs use `PublicId` (GUID); joins and FKs use internal `Id` (bigint). Never expose or accept an internal `Id` (NFR-15). Never return `PasswordHash` / `Salt`.
- **Handlers return `DataOutput<T>` and never throw.** Failures are errors carrying a canonical `PersonMessages` value, which `ResponseResolver` maps through `PersonMessageMap.StatusCodes`.
- **Roles:** `SystemAdmin = 1`, `ScopeAdmin = 2`, `User = 3`; the seeder guarantees `Role.Id == (long)Roles`.
- **Acting user:** the auth middleware attaches `AuthenticatedUser(int Id, int Role)` to `HttpContext.Items["User"]`; the `Id` claim is the person's **internal** `Id`. `PersonController.ApplyActor` copies it onto any `IActorScoped`.
- **No cascade.** SRD §8: logically deleting a person leaves join rows, tokens, and owned applications untouched.
- **Lookup in any deletion state** so AF-09b is an idempotent 200 rather than a 404.
- **Tests:** unit tests use `AsyncFakeRepository<Person>` (one instance as both reader and writer) and Moq for `IScopeOwnershipChecker`; functional tests derive from `WebApiTest<Program>`, join `[Collection(nameof(FunctionalCollection))]`, authorize via `TestTokens`, and assert response **and** database state via `db.CreateContext()`. GWT naming, `// Given` / `// When` / `// Then`, `[UnitFact]` / `[FunctionalFact]`.
- **Run filters:** `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` and `--filter "Category=Functional"`.
- **Commit style:** lowercase Conventional Commits subject, ≤50 chars, imperative; body wrapped at 72; trailer `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

---

## File Structure

**New — production:**
- `src/Application/ArturRios.IdentityManager.Command/Input/DeletePersonCommand.cs`
- `src/Application/ArturRios.IdentityManager.Command/Output/DeletePersonCommandOutput.cs`
- `src/Application/ArturRios.IdentityManager.Command/Handlers/DeletePersonCommandHandler.cs`

**Modified — production:**
- `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessages.cs` — three new messages.
- `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessageMap.cs` — their status codes.
- `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs` — one DELETE action.
- `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs` — handler registration.

**New — tests:**
- `tests/Application/ArturRios.IdentityManager.Command.Tests/DeletePersonCommandHandlerTests.cs`
- `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerDeleteTests.cs`

**Modified — docs:**
- `docs/requirements/Use Case Specification Document.md` — UC-09 brought in line with the behaviour.
- `README.md` — UC-09 marked done in the status tracker (after the merge).

---

## Task 1: Command, output, and messages

The inputs and vocabulary the handler needs. No handler yet, so nothing to unit-test in this task —
the code must compile and the existing suite must stay green.

**Files:**
- Create: `src/Application/ArturRios.IdentityManager.Command/Input/DeletePersonCommand.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Output/DeletePersonCommandOutput.cs`
- Modify: `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessages.cs`
- Modify: `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessageMap.cs`

**Step 1: `DeletePersonCommand`**
- [ ] `public class DeletePersonCommand : BaseCommand, IActorScoped`
- [ ] `Guid Id` — the person's `PublicId`, bound from the route.
- [ ] `long ActingPersonId`, `int ActingRole` — set by the controller from the token.
- [ ] XML doc noting UC-09 and that the acting fields never come from the request body.

**Step 2: `DeletePersonCommandOutput`**
- [ ] `public class DeletePersonCommandOutput : CommandOutput`
- [ ] `Guid Id` — the person's `PublicId`.
- [ ] `bool AlreadyDeleted` — `true` for the AF-09b no-op, `false` for the main flow.

**Step 3: messages**
- [ ] `PersonDeletedSuccessfully` = "Person deleted successfully."
- [ ] `NotAuthorizedToDeletePerson` = "You are not allowed to delete this person."
- [ ] `CannotDeleteSelf` = "You cannot delete your own person record."
- [ ] Each with an XML doc naming its flow, matching the file's existing style.

**Step 4: status map**
- [ ] `PersonDeletedSuccessfully` → `Ok`; `NotAuthorizedToDeletePerson` → `Forbidden`;
      `CannotDeleteSelf` → `Forbidden`. `PersonNotFound` (404) and `ScopeWouldLoseLastOwner` (409)
      are already mapped — do not duplicate them.
- [ ] Update the class XML doc to mention UC-09.

**Verification:**
- [ ] `dotnet build src/ArturRios.IdentityManager.sln` succeeds.
- [ ] `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` still passes.

**Commit:** `feat: add delete person command and messages (UC-09)`

---

## Task 2: Handler (test-first)

**Files:**
- Create: `tests/Application/ArturRios.IdentityManager.Command.Tests/DeletePersonCommandHandlerTests.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Handlers/DeletePersonCommandHandler.cs`

**Step 1: write the failing unit tests** (they will not compile until the handler exists — write the
handler's signature first if that helps, but assert nothing until the tests are in place)

Test-double setup, mirroring `DeleteScopeCommandHandlerTests` and `UpdatePersonCommandHandlerTests`:
one `AsyncFakeRepository<Person>` passed as both reader and writer; a `Mock<IScopeOwnershipChecker>`
returning what the scenario needs; Bogus for entity fields the behaviour does not depend on.

- [ ] `GivenSystemAdmin_WhenHandlingDeletePerson_ThenPersonIsFlaggedDeleted` — main flow, `AlreadyDeleted` false, `UpdatedAt` bumped.
- [ ] `GivenOwningScopeAdmin_WhenHandlingDeletePerson_ThenUserIsFlaggedDeleted` — main flow via `IScopeOwnershipChecker` returning `true`.
- [ ] `GivenScopeAdminWithCoOwnedScopes_WhenHandlingDeletePerson_ThenPersonIsFlaggedDeleted` — a `ScopeAdmin` target whose every owned scope has another owner.
- [ ] `GivenPersonDoesNotExist_WhenHandlingDeletePerson_ThenReturnsPersonNotFoundError` — AF-09a, no write.
- [ ] `GivenPersonAlreadyDeleted_WhenHandlingDeletePerson_ThenReturnsSuccessWithoutWriting` — AF-09b, `AlreadyDeleted` true, `UpdatedAt` unchanged.
- [ ] `GivenSoleOwnerAlreadyDeleted_WhenHandlingDeletePerson_ThenReturnsSuccessInsteadOfConflict` — AF-09b wins over AF-09e (ordering guard).
- [ ] `GivenScopeAdminNotOwningTargetScope_WhenHandlingDeletePerson_ThenReturnsNotAuthorizedError` — AF-09c.
- [ ] `GivenScopeAdminTargetingAnotherScopeAdmin_WhenHandlingDeletePerson_ThenReturnsNotAuthorizedError` — AF-09c.
- [ ] `GivenActorTargetingThemselves_WhenHandlingDeletePerson_ThenReturnsCannotDeleteSelfError` — AF-09d, no write.
- [ ] `GivenSoleOwnerScopeAdmin_WhenHandlingDeletePerson_ThenReturnsScopeWouldLoseLastOwnerError` — AF-09e, no write.

**Step 2: implement `DeletePersonCommandHandler`**
- [ ] Constructor: `IAsyncReadOnlyRepository<Person> personReader`, `IAsyncRepository<Person> personWriter`, `IScopeOwnershipChecker scopeOwnership`. No validator (Decision 5).
- [ ] Load by `PublicId` with **no** `IsDeleted` filter, `Include(ScopeMembership)` and `Include(ScopeOwnerships)` → null is `PersonNotFound`.
- [ ] `command.ActingPersonId == person.Id` → `CannotDeleteSelf`.
- [ ] Authorize: System Admin passes; otherwise require `ActingRole == ScopeAdmin`, `person.RoleId == User`, `person.ScopeMembership is not null`, and `scopeOwnership.ActorMayManageScopeAsync(...)` → else `NotAuthorizedToDeletePerson`.
- [ ] If `person.IsDeleted` → return success with `AlreadyDeleted = true`, no write (AF-09b, checked **before** the NFR-12 guard).
- [ ] NFR-12 guard: when `person.RoleId == ScopeAdmin` and `ScopeOwnerships.Count > 0`, gather co-owned scope ids (`personReader.Query().Where(o => o.Id != person.Id).SelectMany(...).Distinct()`) and return `ScopeWouldLoseLastOwner` if any owned scope is missing from them — the same query `UpdatePersonCommandHandler` runs.
- [ ] Set `IsDeleted = true`, `UpdatedAt = DateTime.UtcNow`, `await personWriter.UpdateAsync(person)`; surface persistence errors.
- [ ] Return `PersonDeletedSuccessfully` with `Id` and `AlreadyDeleted = false`.
- [ ] XML doc summarising the flows the handler serves, matching the sibling handlers' style.

**Verification:**
- [ ] `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` — all pass, including the ten new tests.

**Commit:** `feat: add logical delete person handler (UC-09)`

---

## Task 3: Endpoint and wiring

**Files:**
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs`
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`

**Step 1: controller action**
- [ ] `[HttpDelete("persons/{id:guid}")]` with `[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]`.
- [ ] Build `new DeletePersonCommand { Id = id }`, call `ApplyActor(command)`, dispatch via `commandMediator.ExecuteCommandAsync<DeletePersonCommand, DeletePersonCommandOutput>`.
- [ ] Return `ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes)`.
- [ ] XML doc naming UC-09 and stating that the attribute enforces the role gate while the handler enforces the owner rule.

**Step 2: DI**
- [ ] Register `ICommandHandlerAsync<DeletePersonCommand, DeletePersonCommandOutput>` → `DeletePersonCommandHandler`, next to the `UpdatePersonCommand` registration. No validator.

**Verification:**
- [ ] `dotnet build src/ArturRios.IdentityManager.sln` succeeds.
- [ ] `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` still passes.

**Commit:** `feat: expose logical delete person endpoint (UC-09)`

---

## Task 4: Functional tests

**Files:**
- Create: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerDeleteTests.cs`

**Step 1: seeding helpers** — copy the shape of `PersonControllerUpdateTests`: `SeedScopeAsync`,
`SeedUserAsync(scope)`, `SeedScopeAdminAsync(ownedScope)`, `UniqueEmail`.

**Step 2: tests**, each asserting response **and** database state via `db.CreateContext()`:
- [ ] `GivenSystemAdmin_WhenDeletePerson_ThenPersonIsFlaggedDeleted` — 200, `AlreadyDeleted` false, `is_deleted` true, and the `scope_user` row **still present** (no cascade).
- [ ] `GivenOwningScopeAdmin_WhenDeletePerson_ThenUserIsFlaggedDeleted` — 200.
- [ ] `GivenAlreadyDeletedPerson_WhenDeletePerson_ThenReturnsOkWithoutChangingTheRecord` — 200, `AlreadyDeleted` true, `UpdatedAt` unchanged (AF-09b).
- [ ] `GivenUnknownPersonId_WhenDeletePerson_ThenReturnsNotFound` — 404 (AF-09a).
- [ ] `GivenNonOwningScopeAdmin_WhenDeletePerson_ThenReturnsForbidden` — 403 (AF-09c), row unchanged.
- [ ] `GivenScopeAdminTargetingAnotherScopeAdmin_WhenDeletePerson_ThenReturnsForbidden` — 403 (AF-09c).
- [ ] `GivenActorTargetingThemselves_WhenDeletePerson_ThenReturnsForbidden` — 403 (AF-09d), row unchanged.
- [ ] `GivenSoleOwnerScopeAdmin_WhenDeletePerson_ThenReturnsConflict` — 409 (AF-09e), row unchanged.
- [ ] `GivenUserRole_WhenDeletePerson_ThenReturnsForbidden` — 403 from `[RoleRequirement]`.
- [ ] `GivenNoToken_WhenDeletePerson_ThenReturnsUnauthorized` — 401.

**Verification:**
- [ ] `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Functional"` — all pass.
- [ ] `dotnet test src/ArturRios.IdentityManager.sln` — the full suite is green.

**Commit:** `test: cover logical delete person endpoint (UC-09)`

---

## Task 5: Align the specification

**Files:**
- Modify: `docs/requirements/Use Case Specification Document.md`

**Step 1: UC-09 section**
- [ ] Preconditions: note that the actor may not be the person being deleted.
- [ ] Postconditions: state that join rows, tokens, and owned applications are untouched (SRD §8).
- [ ] Main flow: make the endpoint explicit (`DELETE /api/persons/{id}`) and record that the lookup finds a person in any deletion state so AF-09b can be idempotent.
- [ ] Alternative flows: add `AF-09c` (403, not authorized), `AF-09d` (403, self-deletion), `AF-09e` (409, sole owner of a scope — NFR-12), with a short note explaining why NFR-12 is applied to a logical deletion.

**Verification:**
- [ ] The UC-09 section matches what the code returns, flow for flow.

**Commit:** `docs: align UC-09 spec with implemented behavior`

---

## After the merge (Gate 4)

- [ ] Mark UC-09 ✅ in the README status tracker.
- [ ] Confirm issue [#10](https://github.com/artur-rios/identity-manager-api/issues/10) is in **Done** and closed.
