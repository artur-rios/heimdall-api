# Keep Server-Populated Fields Out of the Public Contract — Design

Date: 2026-08-12

## 1. The defect

The published OpenAPI document declares, on five list operations, a query parameter `ScopeId` that
duplicates the route's `{scopeId}`, alongside `ActingPersonId` and `ActingRole`:

```
GET /api/scopes/{scopeId}/applications
    path  scopeId       (required)
    query ScopeId
    query ActingPersonId
    query ActingRole
    ...
```

None of the three is an input. The controller assigns all three immediately after model binding:

```csharp
public async Task<ActionResult<PaginatedOutput<ApplicationOutput>>> List(
    Guid scopeId, [FromQuery] ListScopeApplicationsQuery query)
{
    query.ScopeId = scopeId;
    HttpContext.ApplyActor(query);
```

So a forged `?ActingRole=1` is discarded — this is a contract defect, not a privilege-escalation
hole, and the existing functional tests (`GivenForgedActorInQueryString_...`) prove it. But the
document advertises three inputs the API does not have, one of which invites a client to contradict
the route and two of which describe the server's own authorization state as if the caller supplied
it.

### 1.1 The same leak is in the request bodies

The root cause is that the mediator's input DTO doubles as the wire DTO: a command carries both the
fields a client sends and the fields the server fills in, and the framework cannot tell them apart.
Commands are bound with `[FromBody]`, so the server-populated fields land in the request-body
schemas too. Thirteen schemas are affected, e.g.:

```
UpdateApplicationCommand ['scopeId', 'id', 'name', 'ownerId', 'actingPersonId', 'actingRole']
```

where `scopeId` and `id` come from the route and the acting pair from the token — four of six
properties are not inputs.

## 2. Scope

Fix the defect everywhere it reaches the public contract, and make the fields non-bindable rather
than merely undocumented, so the contract and the behaviour agree without depending on the
controller's overwrite line.

**In scope:** the 5 `[FromQuery]`-bound list queries and the 13 `[FromBody]`-bound commands.

**Out of scope:**

- Commands and queries the controller constructs in code (`DeleteApplicationCommand`,
  `GetApplicationByIdQuery`, `AddScopeOwnerCommand`, …). They are never bound from the wire and
  never appear in the document, so they carry no contract risk. Attributing them would be noise.
- `LoginCommand.ScopeId`, `PasswordRecoveryCommand.ScopeId`, `GoogleSignInCommand.ScopeId`. These
  are genuine client inputs on routes that have no `{scopeId}` segment. **`ScopeId` is not
  uniformly server-populated** — which is why the guard in §5 keys off route collisions rather than
  a property-name blocklist.
- Splitting the mediator DTOs into separate wire DTOs. A larger boundary change that this defect
  does not justify.

## 3. Mechanism

**One marker on the DTOs: `[JsonIgnore]`.** `System.Text.Json.Serialization.JsonIgnoreAttribute` is
a BCL attribute, so it compiles in the Application-layer projects where these DTOs live. It reads
as what it means here: *this property is not part of the wire representation*.

**Body-bound properties are then already closed.** The `[FromBody]` binder hands the payload to
`System.Text.Json`, which skips the property, and Swashbuckle builds body schemas from the JSON
contract, so the property leaves the schema at the same time.

**Query-bound properties need one more piece.** MVC's model binder knows nothing of
`System.Text.Json` attributes, so `[JsonIgnore]` alone would leave the query parameters bound and
documented. A small `IBindingMetadataProvider` in the Web API layer closes that:

```csharp
public class ServerPopulatedBindingMetadataProvider : IBindingMetadataProvider
{
    public void CreateBindingMetadata(BindingMetadataProviderContext context)
    {
        if (context.Attributes.OfType<JsonIgnoreAttribute>().Any())
        {
            context.BindingMetadata.IsBindingAllowed = false;
        }
    }
}
```

`IsBindingAllowed = false` is exactly what `[BindNever]` sets. The model binder then skips the
property, and `ApiExplorer` — which Swashbuckle reflects over to build the parameter list — omits
non-bindable properties from the operation. One attribute, both halves, on both paths.

### 3.1 Why not `[BindNever]` directly

`Microsoft.AspNetCore.Mvc.ModelBinding.BindNeverAttribute` would do the query half in one line, but
it lives in the ASP.NET Core shared framework, reachable only through
`<FrameworkReference Include="Microsoft.AspNetCore.App" />`. `ArturRios.Heimdall.Query.csproj` is a
plain `Microsoft.NET.Sdk` class library referencing only Domain and Shared, and a `FrameworkReference`
does not flow backwards from the Web SDK project that consumes it. Using it would mean giving an
Application-layer project a dependency on the entire ASP.NET Core framework — which no project under
`src/Application` currently has. The metadata provider keeps that dependency in the layer that
already owns HTTP concerns.

### 3.2 The provider must be registered twice

`tools/ArturRios.Heimdall.OpenApiGen/Program.cs` deliberately does not use the API's `Startup` — it
calls its own `AddControllers().AddApplicationPart(...)` so it can generate the document without a
database or a port. It already shares `SwaggerConfiguration.Configure` with the running API so that
both produce the same document; model-binding configuration now has to be shared the same way, or
the running API would unbind the properties while the generated document still advertised them.

The registration therefore lives in one place both call — a `ModelBindingConfiguration.Configure`
that takes `MvcOptions` — invoked from `Startup.ConfigureWebApi` and from the generator's
`AddControllers`.

Controllers need no change. Every affected controller action already assigns each of these fields
after binding.

### 3.3 Verify the mechanism before applying it in bulk

That Swashbuckle 10.2.3 honours this end-to-end in this generator setup is a prediction, not a
confirmed fact. **First implementation step:** build the provider, wire it into both registration
sites, apply `[JsonIgnore]` to `ListScopeApplicationsQuery` and `UpdateApplicationCommand` only, run
`python3 scripts/openapi.py`, and read the document. Expect
`GET /api/scopes/{scopeId}/applications` to lose three query parameters and
`UpdateApplicationCommand` to lose four properties.

If the query half does not take, the fallback is a Swashbuckle `IOperationFilter` for the document
plus the provider for the binding. Do not proceed to the remaining DTOs until the two-file result is
confirmed.

## 4. The change

### 4.1 Queries — `[JsonIgnore]` on `ScopeId`, `ActingPersonId`, `ActingRole`

`src/Application/ArturRios.Heimdall.Query/Input/`:

| Query | Operation that loses the parameters |
| --- | --- |
| `ListScopeApplicationsQuery` | `GET /api/scopes/{scopeId}/applications` |
| `ListScopeGoogleUsersQuery` | `GET /api/scopes/{scopeId}/google-users` |
| `ListScopePersonsQuery` | `GET /api/scopes/{scopeId}/persons` |
| `ListScopeOwnersQuery` | `GET /api/scopes/{scopeId}/owners` |
| `ListScopePermissionsQuery` | `GET /api/scopes/{scopeId}/permissions` |

`Name`, `Email`, `OwnerId`, `IncludeDeleted`, `PageNumber` and `PageSize` are real inputs and stay
bindable. `ListScopesQuery` is untouched — it has neither a route scope nor acting fields.

### 4.2 Commands — `[JsonIgnore]` on the route- and token-supplied properties

`src/Application/ArturRios.Heimdall.Command/Input/`:

| Command | Properties leaving the schema |
| --- | --- |
| `CreateApplicationCommand` | `ScopeId`, `ActingPersonId`, `ActingRole` |
| `UpdateApplicationCommand` | `ScopeId`, `Id`, `ActingPersonId`, `ActingRole` |
| `CreateUserCommand` | `ScopeId`, `ActingPersonId`, `ActingRole` |
| `CreateScopeOwnerCommand` | `ScopeId`, `ActingPersonId`, `ActingRole` |
| `UpdatePersonCommand` | `Id`, `ActingPersonId`, `ActingRole` |
| `CreateScopePermissionCommand` | `ScopeId`, `ActingPersonId`, `ActingRole` |
| `UpdateScopePermissionCommand` | `ScopeId`, `Id`, `ActingPersonId`, `ActingRole` |
| `UpdateScopeCommand` | `Id` |
| `SetGoogleSignInCommand` | `Id`, `ActingPersonId`, `ActingRole` |
| `EnableTwoFactorAuthCommand` | `ActingPersonId`, `ActingRole` |
| `ConfirmTwoFactorAuthCommand` | `ActingPersonId`, `ActingRole` |
| `DisableTwoFactorAuthCommand` | `ActingPersonId`, `ActingRole` |
| `RegenerateRecoveryCodesCommand` | `ActingPersonId`, `ActingRole` |

`UpdateScopeCommand` has no acting fields but does publish a route-supplied `id`
(`ScopeController.Update` assigns `command.Id = id`), so it belongs to the same defect.

### 4.3 XML documentation

Several of these DTOs carry a class-level remark of the form *"`ScopeId` comes from the route;
`ActingPersonId`/`ActingRole` are set by the controller from the authenticated caller and are never
taken from the request."* That statement stays true and becomes enforced rather than aspirational.
Where a property has its own `<summary>` describing it as a filter the caller supplies, reword it to
say the server assigns it.

### 4.4 Regenerate the document

`python3 scripts/openapi.py` writes `docs/openapi/heimdall.json`, the one committed copy.
(`docs/public/openapi/heimdall.json` is Hugo build output under the gitignored `docs/public/` and
needs no attention.) The `check-openapi` workflow fails the build
if the committed document does not match what the code generates, so the regenerated file must be
part of the same commit.

## 5. Guard against regression

A new `[UnitFact]` in `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/OpenApiContractTests.cs`
reads the committed `docs/openapi/heimdall.json` and asserts, for every operation:

1. **No query parameter name collides with one of that operation's path parameter names**
   (case-insensitive). This is what catches the `ScopeId`/`{scopeId}` duplication generically,
   without assuming `ScopeId` is always server-populated.
2. **No query parameter and no request-body schema property is named `actingPersonId` or
   `actingRole`.** These are server-populated on every endpoint without exception.

The assertions report every violating operation at once, not just the first, so a future regression
names all its damage in one run.

`[UnitFact]`, not `[Fact]`: the CI suites select on `Category=Unit` and `Category=Functional`, so an
uncategorised fact would run in neither. The test needs no database and no host — it reads a file.
It locates the repository root by walking up from `AppContext.BaseDirectory` to the directory
containing `src/ArturRios.Heimdall.sln`.

This one document-level guard replaces a DTO-level reflection test over `IActorScoped`
implementers. The document test subsumes it for everything that reaches the contract, and the
reflection test would have flagged the code-constructed commands of §2 that pose no risk.

## 6. Test changes

**Two existing tests go vacuous and must be rewritten.**
`ApplicationControllerUpdateTests.GivenForgedActingRoleInBody_WhenPutApplication_ThenItIsIgnored`
and `ScopePermissionControllerUpdateTests.GivenForgedActingRoleInBody_WhenPutScopePermission_ThenItIsIgnored`
forge the acting fields by setting them on a typed command and letting the gateway serialize it:

```csharp
var body = Body(coOwner.PublicId, "Hijacked");
body.ActingRole = (int)Roles.SystemAdmin;
body.ActingPersonId = owner.PublicId;
```

Once the properties carry `[JsonIgnore]`, serialization drops them, the request no longer contains
the forged values, and the test passes while proving nothing. Rewrite both to post raw JSON — an
anonymous object carrying `actingRole`/`actingPersonId` alongside the real fields — so they keep
asserting that the server ignores a forged actor arriving on the wire. The assertions
(`Forbidden`, and the entity unchanged) do not change.

**Three query-string forgery tests need no change and gain value.**
`ApplicationControllerListTests`, `ScopePermissionControllerListTests` and
`PersonControllerListScopePersonsTests` already build the forged request as a raw URL string, so
they exercise the new binding-metadata path unmodified.

**Everything else.** No functional test sets a route- or token-supplied property on a posted body
(verified by grep across `tests/Presentation`), so the remaining suites are unaffected. Handler unit
tests construct commands and queries in code and are untouched by both attributes.

## 7. Client artifacts

`api-client/` holds hand-written Bruno collections and `.http` files, not generated code. Checked:
no request there sends `ScopeId`, `actingPersonId` or `actingRole`, so nothing needs changing. Noted
because it would otherwise be an easy omission — a stale example that contradicts the contract.

## 8. Verification

1. `python3 scripts/openapi.py --check` passes (document matches the code).
2. The 5 list operations declare only `Name`/`Email`/`OwnerId`/`IncludeDeleted`/`PageNumber`/
   `PageSize`; the 13 schemas expose only client-supplied properties.
3. `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"` passes, including the new
   `OpenApiContractTests`.
4. `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"` passes, including the two
   rewritten forgery tests — which must be observed to fail if the `[JsonIgnore]` attributes are
   reverted, confirming they still test what they claim.
