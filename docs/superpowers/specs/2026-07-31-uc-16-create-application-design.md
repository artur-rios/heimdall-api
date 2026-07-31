# UC-16: Create Application — Design

## Summary

Implement UC-16 (Create Application, FR-AP-01/02/03): register a non-person identity — an
application representing another system — inside a scope, owned by exactly one person.

| Method | Endpoint | Actors |
| --- | --- | --- |
| POST | `/api/scopes/{scopeId}/applications` | System Admin, Scope Admin (owner of the scope), User (self as owner) |

The postcondition is a single write: one `application` row with `IsDeleted = false`, pointing at the
target scope and at the owning person.

This is the **first use case on the `Application` aggregate**. The entity
(`Domain/Entities/Application.cs`), its EF map (`ApplicationDbMap`), the `Applications` `DbSet`, and
the `application` table in the `InitialCreate` migration all landed with the data-infrastructure
work, so **no migration is required** — this use case adds only the command side and the endpoint.
It is also the first controller in the project whose whole route is scope-nested
(`api/scopes/{scopeId}/applications`), and the first message family for applications.

## Shape

| Artifact | File |
| --- | --- |
| `CreateApplicationCommand` | `…Command/Input/CreateApplicationCommand.cs` |
| `CreateApplicationCommandValidator` | `…Command/Input/Validation/CreateApplicationCommandValidator.cs` |
| `CreateApplicationCommandHandler` | `…Command/Handlers/CreateApplicationCommandHandler.cs` |
| `CreateApplicationCommandOutput` | `…Command/Output/CreateApplicationCommandOutput.cs` |
| `ApplicationMessages` + `ApplicationMessageMap` | `…Shared/Messages/` |
| `ApplicationController` | `…WebApi/Controllers/ApplicationController.cs` |
| DI | `…WebApi/Startup.cs` |

`CreateApplicationCommand` carries `Name` and `OwnerId` from the body, `ScopeId` from the route, and
`IActorScoped`'s `ActingPersonId` / `ActingRole` from the bearer token — never from the body, exactly
as `CreateUserCommand` does.

## Handler flow

| Step | Behavior | Flow |
| --- | --- | --- |
| 1 | Validate `Name` and `OwnerId` | UC-16 step 2, AF-16d |
| 2 | Scope exists and `!IsDeleted` → else `Scope not found.` (404) | UC-16 step 3, AF-16a |
| 3 | Actor rule: System Admin passes; Scope Admin must own the scope (403); User must have named themself (403) | Decision 1, AF-16c |
| 4 | Owner is a non-deleted person who is a `SCOPE_USER` of the scope **or** a `SCOPE_OWNER` of it → else `Owner is not a valid member or owner of the scope.` (400) | UC-16 step 4, AF-16b, FR-AP-03 |
| 5 | Insert the `application` row (`IsDeleted = false`) | UC-16 step 5, FR-AP-01/02 |
| 6 | Return `Application created successfully.` (201) | UC-16 step 6 |

Step 3 is the only branch on the acting role, and it branches three ways because the three actors
have genuinely different rules in the authorization matrix. Step 4 is one query that expresses
FR-AP-03 directly: `!IsDeleted && (ScopeMembership.ScopeId == scope.Id || ScopeOwnerships.Any(o =>
o.ScopeId == scope.Id))`.

## Decisions

1. **The endpoint carries no `[RoleRequirement]`; the handler enforces all three actor rules.**

   Two documents say a `User` may create an application: UC-16 lists `User` among its actors and
   defines AF-16c — a `403` for a User who names *someone else* as owner, which only makes sense if a
   User naming *themself* succeeds — and the SRD §7 authorization matrix says `Create Application |
   ✅ | ✅ (owned scope) | ✅ (self as owner) | ❌`. One column of one table disagrees: SRD §5.3 marks
   the endpoint `ScopeAdmin+`. **This design follows UC-16 and §7 and lets an authenticated `User`
   call it.** Raised at Gate 1 — §5.3's cell looks like the document defect, since it is the only
   place that excludes the `User` and it contradicts an alternative flow that would otherwise be
   unreachable.

   Consequence: like `PersonController.Update` and `GetById`, the action is open to any authenticated
   caller and the per-actor rule lives in the handler, where the data needed to decide it is.

   *Alternative rejected:* `[RoleRequirement(SystemAdmin, ScopeAdmin)]` per §5.3. It would make AF-16c
   dead code — a `User` could never reach the handler to be refused for naming another owner.

2. **A Scope Admin who does not own the target scope gets `403`, not `404`.** UC-16 defines no
   alternative flow for it (AF-16a is about the scope not existing at all), but the matrix's "✅ (owned
   scope)" has to mean something. `403` with `You are not an owner of the target scope.` is the answer
   UC-06 AF-06e and UC-07 AF-07b already give for exactly this fact, through the same
   `IScopeOwnershipChecker`. Reusing the checker also means the rule has one implementation and one
   test class (`ScopeOwnershipCheckerTests`).

   *Alternative rejected:* answering `404` to hide the scope's existence. Nothing in the specification
   asks this endpoint to be non-enumerable, and UC-06 — the same shape of request — answers `403`.

3. **The `User` self-owner rule is decided from the command alone, before any lookup.** For a `User`
   actor, `command.OwnerId != command.ActingPersonId` is refused with `403` (AF-16c) without reading
   the owner. The rule does not depend on whether the named person exists, so checking it first keeps
   AF-16c from doubling as an existence oracle: a User probing other people's ids gets the same `403`
   whether or not they guessed a real one.

4. **A `User` acting on a scope they do not belong to falls out of AF-16b as a `400`, deliberately.**
   They pass Decision 3 (they named themself), then fail step 4, because they are not a `SCOPE_USER`
   of *that* scope. AF-16b's text covers it exactly — "Owner … not associated with the scope" — and
   the owner in question is the caller. No extra rule is invented for the case.

5. **Application names are not unique.** FR-AP-01 lists the fields and imposes no uniqueness, UC-16
   defines no duplicate-name alternative flow (contrast UC-01 AF-01a for scopes), and `ApplicationDbMap`
   declares no unique index on `name`. Two applications may share a name; they are addressed by
   `PublicId`.

6. **A `SystemAdmin` may own nothing, so it can never be an owner — and that needs no special case.**
   FR-AP-03 restricts ownership to a `User` of the scope or a `ScopeAdmin` who owns it. A `SystemAdmin`
   person has neither a `SCOPE_USER` nor a `SCOPE_OWNER` row (SRD §4.2), so step 4's single query
   already refuses them with `400`. A System Admin *acting* may still create an application — for a
   valid owner — which is what the matrix's "✅" means.

7. **Messages get their own family: `ApplicationMessages` / `ApplicationMessageMap`.** Every aggregate
   in the project has one (`Scope`, `Person`, `Auth`), each with its own status dictionary, and UC-17…
   UC-20 will add to this one. Two strings — `Scope not found.` and `You are not an owner of the
   target scope.` — repeat values that also exist in `PersonMessages`; that is fine and already
   precedented by `AuthMessages.PersonNotFound` (UC-15 Decision 3), because the maps are separate
   dictionaries and a controller passes exactly one of them.

8. **The output exposes only public identifiers.** `Id`, `Name`, `ScopeId`, `OwnerId`, `CreatedAt` —
   with `ScopeId` and `OwnerId` being the scope's and owner's `PublicId`, never the internal `bigint`
   foreign keys (SRD §4.0). `CreateScopeCommandOutput` and `CreatePersonCommandOutput` do the same.

9. **`IsDeleted` is set explicitly to `false` rather than left to the column default.** The database
   default exists, but UC-16 step 5 states the value, and stating it in the handler means the created
   entity returned to the caller and the row written agree without a reload.

## Alternative flows → failure paths

| Flow | Condition | Path | Response |
| --- | --- | --- | --- |
| AF-16d | Invalid input | `CreateApplicationCommandValidator` fails | `400` `Application name is required.` / `Application name must be at most 200 characters.` / `Owner is required.` |
| AF-16a | Scope missing or logically deleted | scope lookup returns `null` | `404` `Scope not found.` |
| AF-16c | `User` named an owner other than themself | `ActingRole == User && OwnerId != ActingPersonId` | `403` `You may only create applications you own.` |
| (Decision 2) | `ScopeAdmin` does not own the scope | `IScopeOwnershipChecker` returns `false` | `403` `You are not an owner of the target scope.` |
| AF-16b | Owner missing, deleted, or not tied to the scope | owner lookup returns `null` | `400` `Owner is not a valid member or owner of the scope.` |
| (precondition) | Not authenticated | middleware | `401` |

## Test coverage

**Unit — `CreateApplicationCommandHandlerTests`:** main flow for a System Admin with a `SCOPE_USER`
owner and with a `SCOPE_OWNER` owner (FR-AP-03's two legs); main flow for an owning Scope Admin; main
flow for a User naming themself; AF-16a (missing scope, logically deleted scope); AF-16c (User names
another person — refused without the person existing, Decision 3); Decision 2 (Scope Admin the checker
rejects); AF-16b (owner absent, owner logically deleted, owner belongs to a *different* scope, owner
is a `SystemAdmin` with no membership — Decision 6); AF-16d (validator failure short-circuits before
any lookup); and that the persisted row carries `IsDeleted = false` and the scope's and owner's
internal ids.

**Unit — `CreateApplicationCommandValidatorTests`:** name required, name at 200 and at 201 characters,
owner id required (`Guid.Empty` rejected), and a valid command passing.

**Functional — `ApplicationControllerCreateTests`:** main flow for all three actors, asserting the
response *and* the `application` row (its `scope_id`, `owner_id`, `is_deleted`); AF-16a; AF-16b;
AF-16c; the Scope Admin non-owner `403`; AF-16d; the anonymous `401`; a User creating in a scope they
do not belong to (Decision 4 → `400`); and that two applications may share a name (Decision 5).

## Not in scope

- **Reading, updating, or deleting applications.** UC-17 through UC-20; this use case adds no query
  handler and no `GET`.
- **A per-scope application limit or quota.** No requirement defines one.
- **Applications as authenticating identities** (client credentials, secrets). UC-16 registers a
  record; nothing in FR-AP-01…09 gives an application a way to sign in.
