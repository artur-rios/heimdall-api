# UC-17: View Application — Implementation Plan

Design: [2026-07-31-uc-17-view-application-design.md](../specs/2026-07-31-uc-17-view-application-design.md)
Issue: [#18](https://github.com/artur-rios/identity-manager-api/issues/18)
Branch: `feature/uc-17-view-application`

Sequenced test-first per the
[Testing Specification Document](../../requirements/Testing%20Specification%20Document.md) §9.

Steps 0–3 are the **ownership correction** (application owners are `ScopeAdmin` persons who own the
scope; a `User` may neither own nor create one). Steps 4–11 are UC-17 proper, built on the corrected
rule.

---

## Part A — Ownership correction

### Step 0 — Requirement documents

`docs/requirements/System Requirements Document.md`:

- §2 glossary, "Application": owned by exactly one `ScopeAdmin` who owns its scope.
- **FR-AP-03**: "Every application must have exactly **one** owner, which must be an existing,
  non-logically-deleted `ScopeAdmin` person who owns the application's scope."
- §5.3: `POST …/applications` → `ScopeAdmin (owner, self as owner)+`; `GET …/applications` →
  `ScopeAdmin (owner)+`; `GET …/applications/{id}` → `ScopeAdmin (owner of the application)+`;
  `PUT` / `DELETE` rows drop the `User` reading.
- §7 authorization matrix — both the mermaid block and the table:
  `Create Application | ✅ | ✅ (owned scope, self as owner) | ❌ | ❌`,
  `Read/Update/Delete Application | ✅ | ✅ (owned) | ❌ | ❌`.
- §8 cascade notes: hard-deleting a `User` no longer removes applications; hard-deleting a
  `ScopeAdmin` removes the applications they own; the Google User note stops citing `User` ownership.

`docs/requirements/Use Case Specification Document.md`:

- §1 actor diagram: drop `U --> UC16 & UC17 & UC18 & UC19`.
- **UC-16**: actors → System Admin, Scope Admin; preconditions; main flow step 4; the sequence
  diagram's owner-verification line; AF-16b ("Owner is not a `ScopeAdmin` who owns the scope");
  AF-16c ("Scope Admin attempts to set an owner other than themself").
- **UC-17**: actors → System Admin, Scope Admin; main flow step 2 (System Admin: any scope; Scope
  Admin: only applications they own).
- **UC-18**, **UC-19**: actors and the authorization steps that named the owning `User`.
- UC-20 is already System Admin only — unchanged.

`docs/superpowers/specs/2026-07-31-uc-16-create-application-design.md`: "Superseded in part" banner
naming this design as the correction, with Decisions 1, 3, 4 and 6 marked reversed.

- Verify: no remaining hit for a `User` owning an application —
  `rg -n "self as owner|belonging to the application|a \`User\` belonging" docs/requirements`.

### Step 1 — Corrected UC-16 tests (red)

`CreateApplicationCommandHandlerTests` — replace the `User`-actor and `SCOPE_USER`-owner cases:

| Test | Covers |
| --- | --- |
| `GivenSystemAdminAndScopeOwnerOwner_WhenHandlingCreateApplication_ThenApplicationIsCreated` | main flow |
| `GivenOwningScopeAdminNamingThemself_WhenHandlingCreateApplication_ThenApplicationIsCreated` | main flow |
| `GivenScopeAdminNamingACoOwner_WhenHandlingCreateApplication_ThenCannotSetAnotherOwnerIsReported` | AF-16c |
| `GivenScopeAdminWhoDoesNotOwnTheScope_WhenHandlingCreateApplication_ThenNotScopeOwnerIsReported` | matrix |
| `GivenOwnerWithUserRole_WhenHandlingCreateApplication_ThenOwnerNotValidIsReported` | AF-16b, FR-AP-03 |
| `GivenOwnerScopeAdminOfADifferentScope_WhenHandlingCreateApplication_ThenOwnerNotValidIsReported` | AF-16b |
| `GivenUnknownOwner_…` / `GivenLogicallyDeletedOwner_…` | AF-16b (kept, owner reseeded as a ScopeAdmin) |
| `GivenMissingScope_…` / `GivenLogicallyDeletedScope_…` / `GivenInvalidInput_…` | AF-16a, AF-16d (unchanged) |

Removed: `GivenSystemAdminAndScopeUserOwner_…`, `GivenUserNamingThemself_…`,
`GivenUserNamingAnotherPerson_…`, `GivenUserNamingANonExistentPerson_…`,
`GivenSystemAdminAsOwner_…` (Decision 6 of the UC-16 design no longer holds).

### Step 2 — Corrected UC-16 implementation (green)

- `ApplicationMessages.OwnerNotValidForScope` → `"Owner must be a Scope Admin who owns the target
  scope."`; doc comments on it and on `CannotSetAnotherOwner` rewritten. `ApplicationMessageMap` keys
  and statuses unchanged.
- `CreateApplicationCommandHandler` — the six-step flow from the design: validate → scope → ownership
  checker → Scope Admin self-owner → owner is a non-deleted `ScopeAdmin` with a `SCOPE_OWNER` row on
  this scope → insert.
- `ApplicationController.Create` — add `[RoleRequirement((int)Roles.SystemAdmin,
  (int)Roles.ScopeAdmin)]`; rewrite the XML doc.
- Verify: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` green.

### Step 3 — Corrected UC-16 functional tests

`ApplicationControllerCreateTests` — `SeedUserAsync` stays (a `User` is now only ever a *rejected*
owner) and a `SeedScopeAdminAsync(ownedScope:)` owner becomes the happy path.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenPostApplications_ThenApplicationIsCreated` | main flow (owner = owning ScopeAdmin) |
| `GivenOwningScopeAdminNamingThemself_WhenPostApplications_ThenApplicationIsCreated` | main flow |
| `GivenScopeAdminNamingACoOwner_WhenPostApplications_ThenForbidden` | AF-16c |
| `GivenUserRole_WhenPostApplications_ThenForbidden` | new — `[RoleRequirement]` |
| `GivenOwnerWithUserRole_WhenPostApplications_ThenBadRequest` | AF-16b, FR-AP-03 |
| existing AF-16a / AF-16b / AF-16d / 401 / duplicate-name tests | unchanged apart from the owner seed |

Removed: `GivenUserNamingThemself_…`, `GivenUserNamingAnotherPerson_…`, `GivenUserOfADifferentScope_…`.

- Verify: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Functional"` green.

---

## Part B — UC-17

### Step 4 — Messages and status map

- `ApplicationMessages` — add `ApplicationRetrievedSuccessfully`
  (`"Application retrieved successfully."`), `ApplicationsRetrievedSuccessfully`
  (`"Applications retrieved successfully."`), `ApplicationNotFound` (`"Application not found."`),
  `NotAuthorizedToViewApplication` (`"You are not allowed to view this application."`).
- `ApplicationMessageMap` — the two retrieval messages → 200, `ApplicationNotFound` → 404,
  `NotAuthorizedToViewApplication` → 403.

### Step 5 — Queries and output

- `GetApplicationByIdQuery : BaseQuery, IActorScoped` — `ScopeId`, `Id`, `IncludeDeleted`;
  `ActingPersonId` / `ActingRole`.
- `ListScopeApplicationsQuery : BaseQuery, IActorScoped` — `ScopeId`, `Name?`, `OwnerId?`,
  `IncludeDeleted`; `ActingPersonId` / `ActingRole`.
- `ApplicationOutput : QueryOutput` — `Id`, `Name`, `ScopeId`, `OwnerId`, `IsDeleted`, `CreatedAt`,
  `UpdatedAt` (Decision 11).

### Step 6 — `GetApplicationByIdQueryHandler` tests (red)

`tests/Application/…Query.Tests/GetApplicationByIdQueryHandlerTests.cs`, mirroring
`GetPersonByIdQueryHandlerTests` with a `FakeRepository<Application>`.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenHandlingGetApplicationById_ThenApplicationIsReturned` | main flow |
| `GivenOwningScopeAdmin_WhenHandlingGetApplicationById_ThenApplicationIsReturned` | main flow |
| `GivenReturnedApplication_WhenHandlingGetApplicationById_ThenOutputCarriesPublicIdentifiers` | SRD §4.0 |
| `GivenScopeAdminWhoOwnsTheScopeButNotTheApplication_WhenHandlingGetApplicationById_ThenNotAuthorizedIsReported` | AF-17b, Decision 2 |
| `GivenUnrelatedScopeAdmin_WhenHandlingGetApplicationById_ThenNotAuthorizedIsReported` | AF-17b |
| `GivenUnknownApplication_WhenHandlingGetApplicationById_ThenApplicationNotFoundIsReported` | AF-17a |
| `GivenApplicationOfADifferentScope_WhenHandlingGetApplicationById_ThenApplicationNotFoundIsReported` | AF-17a, Decision 3 |
| `GivenUnknownScope_WhenHandlingGetApplicationById_ThenApplicationNotFoundIsReported` | AF-17a, Decision 3 |
| `GivenDeletedApplicationAndIncludeDeletedFalse_WhenHandlingGetApplicationById_ThenApplicationNotFoundIsReported` | AF-17a, FR-AP-09 |
| `GivenDeletedApplicationAndIncludeDeletedTrue_WhenHandlingGetApplicationById_ThenApplicationIsReturned` | FR-AP-09 |

### Step 7 — `GetApplicationByIdQueryHandler` (green)

Private projection carrying the owner's `PublicId` beside the `ApplicationOutput`; miss →
`ApplicationNotFound`; `SystemAdmin || owner == actor` → else `NotAuthorizedToViewApplication`;
success message `ApplicationRetrievedSuccessfully`. Each step commented with the UC/AF it implements.

### Step 8 — `ListScopeApplicationsQueryHandler` tests (red)

`tests/Application/…Query.Tests/ListScopeApplicationsQueryHandlerTests.cs`, mirroring
`ListScopePersonsQueryHandlerTests` with `FakeRepository<Scope>` / `<Application>` and a Moq
`IScopeOwnershipChecker`.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenHandlingListScopeApplications_ThenEveryApplicationInTheScopeIsReturned` | main flow, Decision 5 |
| `GivenOwningScopeAdmin_WhenHandlingListScopeApplications_ThenOnlyTheirOwnAreReturned` | main flow, Decision 2 |
| `GivenApplicationsOfAnotherScope_WhenHandlingListScopeApplications_ThenTheyAreNotReturned` | FR-AP-05 |
| `GivenMissingScope_WhenHandlingListScopeApplications_ThenScopeNotFoundIsReported` | AF-17a |
| `GivenLogicallyDeletedScope_WhenHandlingListScopeApplications_ThenScopeNotFoundIsReported` | AF-17a |
| `GivenScopeAdminWhoDoesNotOwnTheScope_WhenHandlingListScopeApplications_ThenNotScopeOwnerIsReported` | AF-17b |
| `GivenDeletedApplications_WhenHandlingListScopeApplications_ThenTheyAreExcludedByDefault` | FR-AP-09 |
| `GivenIncludeDeleted_WhenHandlingListScopeApplications_ThenDeletedApplicationsAreReturned` | FR-AP-09 |
| `GivenNameFilter_WhenHandlingListScopeApplications_ThenOnlyMatchingApplicationsAreReturned` | FR-AP-05 |
| `GivenOwnerFilter_WhenHandlingListScopeApplications_ThenOnlyThatOwnersApplicationsAreReturned` | FR-AP-05 |
| `GivenPageSize_WhenHandlingListScopeApplications_ThenResultsArePagedByName` | FR-AP-05 |

### Step 9 — `ListScopeApplicationsQueryHandler` (green)

Scope lookup → ownership check → owner narrowing for a non-System-Admin actor → filters → project →
`PaginateAsync(…, x => x.Name)` with `ApplicationsRetrievedSuccessfully`.

- Verify: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` green.

### Step 10 — Endpoints and DI

- `ApplicationController` — take `QueryMediator` alongside `CommandMediator`; add
  `[HttpGet("{id:guid}")] GetById` and `[HttpGet] List`, both
  `[RoleRequirement(SystemAdmin, ScopeAdmin)]`. Both bind the route `scopeId`, call
  `HttpContext.ApplyActor(query)`, and resolve through `ApplicationMessageMap.StatusCodes`. XML docs
  naming UC-17, FR-AP-04/05/09, and AF-17a/b.
- `Startup.AddDependencies` — register
  `IQueryHandlerAsync<GetApplicationByIdQuery, ApplicationOutput>` and
  `IPaginatedQueryHandlerAsync<ListScopeApplicationsQuery, ApplicationOutput>`.

### Step 11 — Functional tests

`tests/Presentation/…WebApi.Tests/ApplicationControllerGetByIdTests.cs` and
`ApplicationControllerListTests.cs`, reusing the seeding shape of `ApplicationControllerCreateTests`
plus a `SeedApplicationAsync`, authorised with `TestTokens.For(person.PublicId, role)`.

`ApplicationControllerGetByIdTests`:

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenGetApplicationById_ThenOkWithApplication` | main flow + payload assertions |
| `GivenOwningScopeAdmin_WhenGetApplicationById_ThenOk` | main flow |
| `GivenScopeAdminWhoOwnsTheScopeButNotTheApplication_WhenGetApplicationById_ThenForbidden` | AF-17b, Decision 2 |
| `GivenUserRole_WhenGetApplicationById_ThenForbidden` | AF-17b, Decision 1 |
| `GivenUnknownApplication_WhenGetApplicationById_ThenNotFound` | AF-17a |
| `GivenApplicationOfAnotherScope_WhenGetApplicationById_ThenNotFound` | AF-17a, Decision 3 |
| `GivenDeletedApplication_WhenGetApplicationById_ThenNotFound` | AF-17a, FR-AP-09 |
| `GivenDeletedApplicationAndIncludeDeleted_WhenGetApplicationById_ThenOk` | FR-AP-09 |
| `GivenNoToken_WhenGetApplicationById_ThenUnauthorized` | precondition |

`ApplicationControllerListTests`:

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenGetApplications_ThenOkWithEveryApplicationInTheScope` | main flow, Decision 5 |
| `GivenOwningScopeAdmin_WhenGetApplications_ThenOkWithOnlyTheirOwn` | main flow, Decision 2 |
| `GivenNonOwningScopeAdmin_WhenGetApplications_ThenForbidden` | AF-17b |
| `GivenUserRole_WhenGetApplications_ThenForbidden` | AF-17b, Decision 1 |
| `GivenUnknownScope_WhenGetApplications_ThenNotFound` | AF-17a |
| `GivenLogicallyDeletedScope_WhenGetApplications_ThenNotFound` | AF-17a |
| `GivenDeletedApplication_WhenGetApplications_ThenItIsExcludedUnlessRequested` | FR-AP-09 |
| `GivenNameFilter_WhenGetApplications_ThenOnlyMatchingApplicationsAreReturned` | FR-AP-05 |
| `GivenOwnerFilter_WhenGetApplications_ThenOnlyThatOwnersApplicationsAreReturned` | FR-AP-05 |
| `GivenPageSize_WhenGetApplications_ThenResultsArePaged` | FR-AP-05 |
| `GivenForgedActingRoleInQueryString_WhenGetApplications_ThenItIsIgnored` | `ApplyActor` |
| `GivenNoToken_WhenGetApplications_ThenUnauthorized` | precondition |

- Verify: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Functional"` green.

---

## Step 12 — Documentation

- `Testing Specification Document.md` §10: add `GetApplicationByIdQueryHandlerTests` and
  `ListScopeApplicationsQueryHandlerTests` to the Query.Tests row, note the new
  `ApplicationController*` functional classes, and update the suite totals line to UC-17.
- `README.md`: mark UC-17 done in the use case tracker; check its prose for any claim that a `User`
  may own an application.

## Step 13 — Full suite

`dotnet test src/ArturRios.IdentityManager.sln` — both categories green before the pull request. The
pull request body records the ownership correction and the deliberate departure from
one-use-case-per-branch.
