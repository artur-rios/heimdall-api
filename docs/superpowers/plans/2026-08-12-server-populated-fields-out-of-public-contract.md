# Keep Server-Populated Fields Out of the Public Contract — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the published OpenAPI document from declaring route- and token-supplied fields as
client inputs, and make those fields non-bindable so the contract and the behaviour agree.

**Architecture:** The mediator's input DTOs double as the wire DTOs, so fields the server fills in
are indistinguishable from fields a client sends. Mark the server-populated properties with the
attribute that excludes them from their binding path — `[BindNever]` for `[FromQuery]`-bound list
queries, `[JsonIgnore]` for `[FromBody]`-bound commands — which removes them from the generated
document and from binding at the same time. Controllers are unchanged; every one already assigns
these fields after binding. A document-level test then guards the contract against regression.

**Tech Stack:** .NET 10, ASP.NET Core MVC controllers, Swashbuckle.AspNetCore 10.2.3, xUnit,
`ArturRios.Util.Test`, `scripts/openapi.py` (Python 3) for document generation.

**Spec:** [2026-08-12-server-populated-fields-out-of-public-contract-design.md](../specs/2026-08-12-server-populated-fields-out-of-public-contract-design.md)

## Global Constraints

- **Controllers are not modified.** Every affected action already assigns each server-populated
  field after model binding. If a task seems to need a controller change, stop — something is wrong.
- **`ScopeId` is not uniformly server-populated.** `LoginCommand.ScopeId`,
  `PasswordRecoveryCommand.ScopeId` and `GoogleSignInCommand.ScopeId` are genuine client inputs on
  routes with no `{scopeId}` segment. Never attribute them.
- **Only wire-bound DTOs are touched.** Commands and queries the controller constructs in code
  (`DeleteApplicationCommand`, `GetApplicationByIdQuery`, `AddScopeOwnerCommand`, …) never appear in
  the document and get no attributes.
- **The document is regenerated in the same commit as the code that changes it.** The
  `check-openapi` CI job regenerates `docs/openapi/heimdall.json` and compares bytes; a commit that
  changes a DTO without the regenerated document fails the build.
- **Regeneration command:** `python3 scripts/openapi.py` (writes `docs/openapi/heimdall.json`).
  `docs/public/` is gitignored Hugo output — never stage it.
- **Test category attributes:** CI selects on `Category=Unit` and `Category=Functional`. Use
  `[UnitFact]` or `[FunctionalFact]` from `ArturRios.Util.Test.Attributes`. A plain `[Fact]` runs in
  neither suite.
- **Functional tests need Docker** (the `PostgresFixture` starts a throwaway container).

## File Structure

**Modified — queries** (`src/Application/ArturRios.Heimdall.Query/Input/`): `ListScopeApplicationsQuery.cs`,
`ListScopeGoogleUsersQuery.cs`, `ListScopePersonsQuery.cs`, `ListScopeOwnersQuery.cs`,
`ListScopePermissionsQuery.cs` — add `[BindNever]` to `ScopeId`, `ActingPersonId`, `ActingRole`.

**Modified — commands** (`src/Application/ArturRios.Heimdall.Command/Input/`): 13 files, listed in
Tasks 1 and 3 — add `[JsonIgnore]` to the route- and token-supplied properties.

**Modified — tests** (`tests/Presentation/ArturRios.Heimdall.WebApi.Tests/`):
`ApplicationControllerUpdateTests.cs` and `ScopePermissionControllerUpdateTests.cs` — rewrite the
two body-forgery tests to post raw JSON, since typed serialization can no longer carry the forged
fields.

**Created:** `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/OpenApiContractTests.cs` — the
regression guard, reading the committed document.

**Regenerated:** `docs/openapi/heimdall.json`.

---

### Task 1: Pilot — prove the two attributes work

The spec deliberately does not assume Swashbuckle 10.2.3 honours `[BindNever]` and `[JsonIgnore]`
end-to-end in this generator setup. This task applies each attribute to exactly one DTO and reads
the regenerated document. **If the document does not change as described in Step 3, stop and report
before touching the other 16 files.**

**Files:**
- Modify: `src/Application/ArturRios.Heimdall.Query/Input/ListScopeApplicationsQuery.cs`
- Modify: `src/Application/ArturRios.Heimdall.Command/Input/UpdateApplicationCommand.cs`
- Modify: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationControllerUpdateTests.cs:347-368`
- Regenerate: `docs/openapi/heimdall.json`

**Interfaces:**
- Consumes: nothing.
- Produces: the attribute pattern Tasks 2 and 3 repeat verbatim — `[BindNever]` from
  `Microsoft.AspNetCore.Mvc.ModelBinding` on query properties, `[JsonIgnore]` from
  `System.Text.Json.Serialization` on command properties.

- [ ] **Step 1: Add `[BindNever]` to the pilot query**

In `ListScopeApplicationsQuery.cs`, add the using and attribute the three server-populated
properties. `Name`, `OwnerId` and `IncludeDeleted` are real filters and stay untouched.

```csharp
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to list the applications of a scope, with pagination and optional filtering (UC-17,
///     FR-AP-05). <see cref="ScopeId" /> comes from the route;
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never taken from the request — a Scope Admin sees only the
///     applications they own, so a forged acting id would be a forged answer. All three are
///     <c>[BindNever]</c>, so the model binder skips them and they never reach the public contract.
/// </summary>
public class ListScopeApplicationsQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope whose applications are listed (assigned from the route).</summary>
    [BindNever]
    public Guid ScopeId { get; set; }

    /// <summary>Optional case-insensitive substring filter on the application's name.</summary>
    public string? Name { get; set; }

    /// <summary>
    ///     Optional filter on the owner's <c>PublicId</c>. Useful to a System Admin narrowing a busy
    ///     scope; inert for a Scope Admin, whose results are already restricted to their own.
    /// </summary>
    public Guid? OwnerId { get; set; }

    /// <summary>When <c>true</c>, logically deleted applications are included (FR-AP-09).</summary>
    public bool IncludeDeleted { get; set; }

    [BindNever]
    public Guid ActingPersonId { get; set; }

    [BindNever]
    public int ActingRole { get; set; }
}
```

- [ ] **Step 2: Add `[JsonIgnore]` to the pilot command**

In `UpdateApplicationCommand.cs`, attribute the four server-populated properties. `Name` and
`OwnerId` are the real body.

```csharp
using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to change an application's name and owner (UC-18, FR-AP-06). The application is
///     addressed by <see cref="Id" /> within <see cref="ScopeId" />, both assigned from the route.
///     PUT semantics: <see cref="Name" /> and <see cref="OwnerId" /> are replaced, so a caller
///     changing only the name resubmits the current owner. The scope is a route qualifier, never a
///     field to write — FR-AP-02 fixes an application's scope at creation time.
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller. All four are <c>[JsonIgnore]</c>, so they are not deserialized from the
///     body and do not appear in the request schema.
/// </summary>
public class UpdateApplicationCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the scope the application belongs to (assigned from the route).</summary>
    [JsonIgnore]
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the application to update (assigned from the route).</summary>
    [JsonIgnore]
    public Guid Id { get; set; }

    /// <summary>New application display name. Required, max 200 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Public identifier of the person that will own the application (FR-AP-03). Verified only
    ///     when it differs from the current owner (UC-18 main flow step 4).
    /// </summary>
    public Guid OwnerId { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
```

- [ ] **Step 3: Regenerate the document and verify the mechanism**

Run:

```bash
python3 scripts/openapi.py && git diff --stat docs/openapi/heimdall.json
```

Then inspect the two affected entries:

```bash
python3 -c "import json; d=json.load(open('docs/openapi/heimdall.json')); print([p['name'] for p in d['paths']['/api/scopes/{scopeId}/applications']['get']['parameters']]); print(list(d['components']['schemas']['UpdateApplicationCommand']['properties']))"
```

Expected output, exactly:

```
['scopeId', 'Name', 'OwnerId', 'IncludeDeleted', 'PageNumber', 'PageSize']
['name', 'ownerId']
```

The first list keeps only the path `scopeId` and the real filters; `ScopeId`, `ActingPersonId` and
`ActingRole` are gone as query parameters. The second keeps only the client-supplied body.

**If either list still contains the server-populated names, the attribute did not take. Stop here
and report which one failed** — the spec's fallback is a Swashbuckle `IOperationFilter` (parameters)
or `ISchemaFilter` (schemas) keyed off the attribute, for that half only, and that is a change worth
discussing before making.

- [ ] **Step 4: Rewrite the body-forgery test so it still forges**

`ApplicationControllerUpdateTests.GivenForgedActingRoleInBody_WhenPutApplication_ThenItIsIgnored`
currently forges by setting properties on a typed `UpdateApplicationCommand` and letting the gateway
serialize it. With `[JsonIgnore]` those properties no longer serialize, so the request would carry no
forged values and the test would pass while proving nothing. Replace the whole test with one that
posts an anonymous object, which serializes every property it declares:

```csharp
    [FunctionalFact]
    public async Task GivenForgedActingRoleInBody_WhenPutApplication_ThenItIsIgnored()
    {
        // Given a Scope Admin claiming SystemAdmin in the body: the acting fields are [JsonIgnore],
        // so they are not deserialized at all, and ApplyActor sets them from the token — the AF-18c
        // refusal still stands. The body is an anonymous object, not an UpdateApplicationCommand,
        // precisely because the typed command can no longer carry the forged fields onto the wire.
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.For(coOwner.PublicId, (int)Roles.ScopeAdmin));
        var body = new
        {
            name = "Hijacked",
            ownerId = coOwner.PublicId,
            actingRole = (int)Roles.SystemAdmin,
            actingPersonId = owner.PublicId,
            scopeId = Guid.NewGuid(),
            id = Guid.NewGuid()
        };

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), body);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(owner.Id, (await StoredAsync(application.PublicId)).OwnerId);
    }
```

The forged `scopeId`/`id` are new to this test and are the point: they prove the route still
addresses the application even when the body contradicts it. If they were honoured, the request
would target a nonexistent application and return 404 rather than 403.

- [ ] **Step 5: Run the affected functional tests**

Run:

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional&FullyQualifiedName~ApplicationController"
```

Expected: PASS, including `GivenForgedActingRoleInBody_WhenPutApplication_ThenItIsIgnored` and the
already-raw-URL `GivenForgedActingRoleInQueryString_WhenGetApplications_ThenItIsIgnored`, which now
exercises the `[BindNever]` path unchanged.

- [ ] **Step 6: Run the unit suite**

Run:

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"
```

Expected: PASS. Handler unit tests construct commands and queries in code, so neither attribute
affects them.

- [ ] **Step 7: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Query/Input/ListScopeApplicationsQuery.cs src/Application/ArturRios.Heimdall.Command/Input/UpdateApplicationCommand.cs tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ApplicationControllerUpdateTests.cs docs/openapi/heimdall.json
git commit -m "fix: stop publishing server-populated fields on the application endpoints"
```

---

### Task 2: The remaining four list queries

**Files:**
- Modify: `src/Application/ArturRios.Heimdall.Query/Input/ListScopeGoogleUsersQuery.cs`
- Modify: `src/Application/ArturRios.Heimdall.Query/Input/ListScopePersonsQuery.cs`
- Modify: `src/Application/ArturRios.Heimdall.Query/Input/ListScopeOwnersQuery.cs`
- Modify: `src/Application/ArturRios.Heimdall.Query/Input/ListScopePermissionsQuery.cs`
- Regenerate: `docs/openapi/heimdall.json`

**Interfaces:**
- Consumes: the `[BindNever]` pattern verified in Task 1.
- Produces: nothing new.

- [ ] **Step 1: Apply `[BindNever]` to all four**

In each file: add `using Microsoft.AspNetCore.Mvc.ModelBinding;` to the using block, and put
`[BindNever]` on `ScopeId`, `ActingPersonId` and `ActingRole`. Leave `Name`, `Email`,
`IncludeDeleted` and the inherited `PageNumber`/`PageSize` alone — they are real filters.

Three of the four — `ListScopeGoogleUsersQuery`, `ListScopePersonsQuery`, `ListScopeOwnersQuery` —
declare `ScopeId`, `Name`, `Email`, `IncludeDeleted`, `ActingPersonId`, `ActingRole`.
`ListScopePermissionsQuery` is the same minus `Email`. Apply this shape to each:

```csharp
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ArturRios.Heimdall.Query.Input;

public class ListScopeGoogleUsersQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope whose Google Users are listed (assigned from the route).</summary>
    [BindNever]
    public Guid ScopeId { get; set; }

    /// <summary>Optional case-insensitive substring filter on the Google User's name.</summary>
    public string? Name { get; set; }

    /// <summary>Optional case-insensitive substring filter on the Google User's email.</summary>
    public string? Email { get; set; }

    /// <summary>When <c>true</c>, logically deleted Google Users are included (FR-GO-17).</summary>
    public bool IncludeDeleted { get; set; }

    [BindNever]
    public Guid ActingPersonId { get; set; }

    [BindNever]
    public int ActingRole { get; set; }
}
```

Each of these four classes already carries a class-level `<summary>` saying `ScopeId` comes from the
route and the acting fields are set by the controller. Extend that sentence in each with: *"All
three are `[BindNever]`, so the model binder skips them and they never reach the public contract."*
Keep the rest of each summary verbatim — the UC/FR references differ per file.

- [ ] **Step 2: Regenerate and verify all five list operations are clean**

Run:

```bash
python3 scripts/openapi.py
python3 -c "
import json
d=json.load(open('docs/openapi/heimdall.json'))
for route in ['applications','google-users','persons','owners','permissions']:
    op=d['paths']['/api/scopes/{scopeId}/'+route]['get']
    print(route, [p['name'] for p in op['parameters']])
"
```

Expected: no list contains `ScopeId`, `ActingPersonId` or `ActingRole`. Each contains the path
`scopeId` plus its own filters and `PageNumber`/`PageSize`:

```
applications ['scopeId', 'Name', 'OwnerId', 'IncludeDeleted', 'PageNumber', 'PageSize']
google-users ['scopeId', 'Name', 'Email', 'IncludeDeleted', 'PageNumber', 'PageSize']
persons ['scopeId', 'Name', 'Email', 'IncludeDeleted', 'PageNumber', 'PageSize']
owners ['scopeId', 'Name', 'Email', 'IncludeDeleted', 'PageNumber', 'PageSize']
permissions ['scopeId', 'Name', 'IncludeDeleted', 'PageNumber', 'PageSize']
```

- [ ] **Step 3: Run the list-endpoint functional tests**

Run:

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional&FullyQualifiedName~List"
```

Expected: PASS. This covers the three query-string forgery tests
(`ApplicationControllerListTests`, `ScopePermissionControllerListTests`,
`PersonControllerListScopePersonsTests`), which build forged requests as raw URL strings and so
need no change. `PersonControllerListScopePersonsTests.GivenForgedActorInQueryString_...` sends
`actingPersonId=1` — an unbindable value for a `Guid`. It expects 403 and still gets it: the
authorization filter short-circuits before model binding either way.

- [ ] **Step 4: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Query/Input docs/openapi/heimdall.json
git commit -m "fix: stop publishing server-populated fields on the remaining list endpoints"
```

---

### Task 3: The remaining twelve command schemas

**Files:**
- Modify, in `src/Application/ArturRios.Heimdall.Command/Input/`:
  `CreateApplicationCommand.cs`, `CreateUserCommand.cs`, `CreateScopeOwnerCommand.cs`,
  `UpdatePersonCommand.cs`, `CreateScopePermissionCommand.cs`, `UpdateScopePermissionCommand.cs`,
  `UpdateScopeCommand.cs`, `SetGoogleSignInCommand.cs`, `EnableTwoFactorAuthCommand.cs`,
  `ConfirmTwoFactorAuthCommand.cs`, `DisableTwoFactorAuthCommand.cs`,
  `RegenerateRecoveryCodesCommand.cs`
- Modify: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ScopePermissionControllerUpdateTests.cs:277-297`
- Regenerate: `docs/openapi/heimdall.json`

**Interfaces:**
- Consumes: the `[JsonIgnore]` pattern verified in Task 1.
- Produces: nothing new.

- [ ] **Step 1: Apply `[JsonIgnore]` per this table**

Add `using System.Text.Json.Serialization;` to each file's using block, then attribute exactly these
properties and no others:

| File | Properties to attribute |
| --- | --- |
| `CreateApplicationCommand.cs` | `ScopeId`, `ActingPersonId`, `ActingRole` |
| `CreateUserCommand.cs` | `ScopeId`, `ActingPersonId`, `ActingRole` |
| `CreateScopeOwnerCommand.cs` | `ScopeId`, `ActingPersonId`, `ActingRole` |
| `UpdatePersonCommand.cs` | `Id`, `ActingPersonId`, `ActingRole` |
| `CreateScopePermissionCommand.cs` | `ScopeId`, `ActingPersonId`, `ActingRole` |
| `UpdateScopePermissionCommand.cs` | `ScopeId`, `Id`, `ActingPersonId`, `ActingRole` |
| `UpdateScopeCommand.cs` | `Id` |
| `SetGoogleSignInCommand.cs` | `Id`, `ActingPersonId`, `ActingRole` |
| `EnableTwoFactorAuthCommand.cs` | `ActingPersonId`, `ActingRole` |
| `ConfirmTwoFactorAuthCommand.cs` | `ActingPersonId`, `ActingRole` |
| `DisableTwoFactorAuthCommand.cs` | `ActingPersonId`, `ActingRole` |
| `RegenerateRecoveryCodesCommand.cs` | `ActingPersonId`, `ActingRole` |

`UpdateScopeCommand` has no acting fields; its `Id` is assigned by `ScopeController.Update` from the
route, which is the same defect.

The edit is uniform, e.g. in `CreateApplicationCommand.cs`:

```csharp
using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

public class CreateApplicationCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the scope the application belongs to (assigned from the route).</summary>
    [JsonIgnore]
    public Guid ScopeId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Public identifier of the person that will own the application (FR-AP-03).</summary>
    public Guid OwnerId { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
```

Where a class-level `<summary>` already says these fields come from the route or the caller, extend
it with: *"They are `[JsonIgnore]`, so they are not deserialized from the body and do not appear in
the request schema."* Keep each summary's UC/FR references verbatim.

**Do not touch** `LoginCommand.ScopeId`, `PasswordRecoveryCommand.ScopeId` or
`GoogleSignInCommand.ScopeId`. Their routes have no `{scopeId}` segment and the client genuinely
supplies the value.

- [ ] **Step 2: Rewrite the second body-forgery test**

`ScopePermissionControllerUpdateTests.GivenForgedActingRoleInBody_WhenPutScopePermission_ThenItIsIgnored`
has the same typed-body problem Task 1 fixed in `ApplicationControllerUpdateTests`. Replace the
whole test:

```csharp
    [FunctionalFact]
    public async Task GivenForgedActingRoleInBody_WhenPutScopePermission_ThenItIsIgnored()
    {
        // Given a Scope Admin claiming SystemAdmin in the body: the acting fields are [JsonIgnore],
        // so they are not deserialized at all, and ApplyActor sets them from the token — the AF-33e
        // refusal still stands. The body is an anonymous object, not an UpdateScopePermissionCommand,
        // precisely because the typed command can no longer carry the forged fields onto the wire.
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope);
        var stranger = await SeedScopeAdminAsync();
        Authorize(TestTokens.For(stranger.PublicId, (int)Roles.ScopeAdmin));
        var body = new
        {
            name = "Hijacked",
            description = (string?)null,
            includeAsJwtClaim = false,
            actingRole = (int)Roles.SystemAdmin,
            actingPersonId = Guid.NewGuid(),
            scopeId = Guid.NewGuid(),
            id = Guid.NewGuid()
        };

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId), body);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(permission.Name, (await StoredAsync(permission.PublicId)).Name);
    }
```

- [ ] **Step 3: Regenerate and verify every command schema is clean**

Run:

```bash
python3 scripts/openapi.py
python3 -c "
import json
d=json.load(open('docs/openapi/heimdall.json'))
bad=[(n,p) for n,s in d['components']['schemas'].items() for p in s.get('properties',{}) if p in ('actingPersonId','actingRole')]
print('acting leaks:', bad)
for n in ['UpdateApplicationCommand','UpdateScopePermissionCommand','UpdateScopeCommand','SetGoogleSignInCommand','CreateUserCommand','LoginCommand']:
    print(n, list(d['components']['schemas'][n]['properties']))
"
```

Expected:

```
acting leaks: []
UpdateApplicationCommand ['name', 'ownerId']
UpdateScopePermissionCommand ['name', 'description', 'includeAsJwtClaim']
UpdateScopeCommand ['name', 'description']
SetGoogleSignInCommand ['enabled']
CreateUserCommand ['name', 'email', 'password']
LoginCommand ['email', 'password', 'scopeId']
```

`LoginCommand` keeping `scopeId` is the check that the exclusion list was respected — it is a real
input and must survive.

- [ ] **Step 4: Run both suites in full**

Run:

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"
```

Expected: PASS.

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"
```

Expected: PASS. This is the first full functional run of the change and is what catches any endpoint
whose handler silently depended on a body-supplied value that is now dropped.

- [ ] **Step 5: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Command/Input tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ScopePermissionControllerUpdateTests.cs docs/openapi/heimdall.json
git commit -m "fix: stop publishing server-populated fields in request bodies"
```

---

### Task 4: The regression guard

The document is clean; nothing stops the next command from re-opening it. This task adds a test over
the published document itself. It is written last because it can only ever be committed green — so
Step 2 proves it detects the defect by running it against the pre-fix document out of git history,
which is the "watch it fail" half of the cycle.

**Files:**
- Create: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/OpenApiContractTests.cs`

**Interfaces:**
- Consumes: `docs/openapi/heimdall.json` as committed.
- Produces: nothing.

- [ ] **Step 1: Write the guard test**

```csharp
using System.Text.Json;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Guards the published contract against the defect fixed on 2026-08-12: the mediator's input DTOs
// double as the wire DTOs, so a property the server assigns from the route or the token is
// indistinguishable, to the framework, from one the client sends. Both leaks it produced are
// checked here, over the document itself rather than over the DTOs — a DTO-level reflection test
// would also flag the commands the controller constructs in code, which never reach the contract at
// all. These are [UnitFact]: no database, no host, just the committed file.
public class OpenApiContractTests
{
    // Server-populated on every endpoint without exception, unlike ScopeId — LoginCommand,
    // PasswordRecoveryCommand and GoogleSignInCommand take a genuine client-supplied ScopeId on
    // routes with no {scopeId} segment, which is why the route-collision test below keys off the
    // operation's own path parameters instead of a name blocklist.
    private static readonly string[] ServerPopulated = ["actingPersonId", "actingRole"];

    private static readonly string[] HttpMethods =
        ["get", "put", "post", "delete", "patch", "options", "head"];

    [UnitFact]
    public void GivenPublishedDocument_WhenOperationsInspected_ThenNothingRepeatsAPathParameter()
    {
        using var document = LoadDocument();
        var violations = new List<string>();

        foreach (var (route, method, operation) in Operations(document))
        {
            var pathNames = ParameterNames(operation, "path");

            violations.AddRange(ParameterNames(operation, "query")
                .Where(name => pathNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Select(name => $"{method} {route}: query parameter '{name}' repeats the route"));

            violations.AddRange(RequestBodyPropertyNames(document, operation)
                .Where(name => pathNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Select(name => $"{method} {route}: body property '{name}' repeats the route"));
        }

        Assert.True(violations.Count == 0, Report(violations));
    }

    [UnitFact]
    public void GivenPublishedDocument_WhenOperationsInspected_ThenNothingExposesTheActingCaller()
    {
        using var document = LoadDocument();
        var violations = new List<string>();

        foreach (var (route, method, operation) in Operations(document))
        {
            violations.AddRange(ParameterNames(operation, "query")
                .Where(IsServerPopulated)
                .Select(name => $"{method} {route}: query parameter '{name}'"));

            violations.AddRange(RequestBodyPropertyNames(document, operation)
                .Where(IsServerPopulated)
                .Select(name => $"{method} {route}: body property '{name}'"));
        }

        Assert.True(violations.Count == 0, Report(violations));
    }

    private static bool IsServerPopulated(string name) =>
        ServerPopulated.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string Report(List<string> violations) =>
        $"The published contract exposes {violations.Count} server-populated field(s):"
        + Environment.NewLine
        + string.Join(Environment.NewLine, violations);

    private static JsonDocument LoadDocument() =>
        JsonDocument.Parse(File.ReadAllText(DocumentPath()));

    // Overridable so the test can be pointed at a document out of git history, which is how it was
    // shown to fail before the fix landed.
    private static string DocumentPath() =>
        Environment.GetEnvironmentVariable("HEIMDALL_OPENAPI_DOCUMENT")
        ?? Path.Combine(RepositoryRoot(), "docs", "openapi", "heimdall.json");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "src", "ArturRios.Heimdall.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException(
                   $"Could not locate the repository root from {AppContext.BaseDirectory}");
    }

    private static IEnumerable<(string Route, string Method, JsonElement Operation)> Operations(
        JsonDocument document)
    {
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject()
                         .Where(candidate => HttpMethods.Contains(candidate.Name)))
            {
                yield return (path.Name, operation.Name.ToUpperInvariant(), operation.Value);
            }
        }
    }

    private static List<string> ParameterNames(JsonElement operation, string location) =>
        operation.TryGetProperty("parameters", out var parameters)
            ? parameters.EnumerateArray()
                .Where(parameter => parameter.GetProperty("in").GetString() == location)
                .Select(parameter => parameter.GetProperty("name").GetString()!)
                .ToList()
            : [];

    private static IEnumerable<string> RequestBodyPropertyNames(
        JsonDocument document, JsonElement operation)
    {
        if (!operation.TryGetProperty("requestBody", out var body)
            || !body.TryGetProperty("content", out var content))
        {
            return [];
        }

        return content.EnumerateObject()
            .Select(media => media.Value.GetProperty("schema"))
            .SelectMany(schema => PropertyNames(document, schema))
            .Distinct();
    }

    private static IEnumerable<string> PropertyNames(JsonDocument document, JsonElement schema)
    {
        var resolved = schema.TryGetProperty("$ref", out var reference)
            ? Resolve(document, reference.GetString()!)
            : schema;

        return resolved.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(property => property.Name).ToList()
            : [];
    }

    private static JsonElement Resolve(JsonDocument document, string reference) =>
        document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(reference[(reference.LastIndexOf('/') + 1)..]);
}
```

- [ ] **Step 2: Prove the test detects the defect**

Extract the pre-fix document from git history and point the test at it. `HEAD~3` is the commit
before Task 1 (Tasks 1, 2 and 3 each made one commit); confirm with `git log --oneline -4` first and
adjust if the history differs.

```bash
git show HEAD~3:docs/openapi/heimdall.json > heimdall-before.json
HEIMDALL_OPENAPI_DOCUMENT="$(pwd)/heimdall-before.json" dotnet test src/ArturRios.Heimdall.sln --filter "FullyQualifiedName~OpenApiContractTests"
```

(PowerShell equivalent: `$env:HEIMDALL_OPENAPI_DOCUMENT = "$PWD/heimdall-before.json"` on its own
line, then the `dotnet test` line, then `Remove-Item Env:HEIMDALL_OPENAPI_DOCUMENT`. The path must
be absolute — the test process runs from its own output directory.)

Expected: **both tests FAIL, with these exact counts:**

- `ThenNothingRepeatsAPathParameter` — **16** violations: the query `ScopeId` on each of the 5 list
  operations, plus 11 body properties (`scopeId` and/or `id` across the 9 create/update operations
  whose route already carries them).
- `ThenNothingExposesTheActingCaller` — **34** violations: 10 query parameters (`ActingPersonId` and
  `ActingRole` on each of the 5 list operations) plus 24 body properties (the acting pair across the
  12 command schemas that carry it).

If either passes, or the counts differ, the assertion logic is wrong and would never catch a
regression — fix it before continuing.

Then delete the scratch file so it cannot be committed:

```bash
rm heimdall-before.json
```

- [ ] **Step 3: Run the test against the current document**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "FullyQualifiedName~OpenApiContractTests"
```

Expected: PASS, both tests.

- [ ] **Step 4: Run the unit suite to confirm the category attribute took**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"
```

Expected: PASS, and the total test count is 2 higher than before. If it is unchanged, the
`[UnitFact]` attribute is missing or wrong and the guard would never run in CI.

- [ ] **Step 5: Confirm the document matches the code**

```bash
python3 scripts/openapi.py --check
```

Expected: exit 0. This is the same check CI runs; it must pass before the branch is opened for
review.

- [ ] **Step 6: Commit**

```bash
git add tests/Presentation/ArturRios.Heimdall.WebApi.Tests/OpenApiContractTests.cs
git commit -m "test: guard the published contract against server-populated fields"
```

---

## Verification (whole change)

- [ ] `python3 scripts/openapi.py --check` exits 0.
- [ ] `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"` passes.
- [ ] `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"` passes.
- [ ] `git diff main --stat` touches only the 17 DTOs, 3 test files and `docs/openapi/heimdall.json`
      — no controller, no handler.
