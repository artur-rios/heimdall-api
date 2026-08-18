# Two-Factor Status and Scope Admin Listing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish two-factor status the API already holds (`GET /api/auth/2fa` plus a `twoFactorEnabled` flag on `PersonOutput`), and add a Scope Admin listing (`GET /api/persons/scope-admins`) so the UI client's owner pickers have a source.

**Architecture:** Three read-side additions to the existing CQRS query layer. Each is a query class, an output class, a handler, a DI registration in `Startup`, and a controller action — the same shape as every other query in `ArturRios.Heimdall.Query`. No command, entity, or migration changes; no new database columns. Authorization stays where the codebase already puts it: role gates as controller attributes, data-dependent rules inside handlers, actor identity supplied by `HttpContext.ApplyActor` and never by the request.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core (PostgreSQL), FluentValidation, xUnit + Moq, `ArturRios.*` internal packages (`Mediator`, `Output`, `Data.Relational.Core`, `Util.Test`).

**Spec:** `docs/superpowers/specs/2026-08-18-2fa-status-and-scope-admin-listing-design.md`

## Global Constraints

- Internal `bigint` `Id` values never leave the data layer (NFR-15). Every identifier on an output type is a `PublicId` (`Guid`).
- Actor fields (`ActingPersonId`, `ActingRole`) are populated by the controller from the bearer token via `HttpContext.ApplyActor`, never bound from the request. Mark them `[JsonIgnore]`.
- Every paginated list query validates page bounds and filter lengths before touching the database (NFR-10), via a validator deriving from `PaginatedQueryValidator<TQuery>`.
- Test method names follow `GivenSomeCondition_WhenSomeAction_ThenSomeOutput`. Unit tests use `[UnitFact]`, functional tests `[FunctionalFact]` from `ArturRios.Util.Test.Attributes`.
- Every list query orders by a non-unique sort key with `Id` as tiebreaker, then paginates over that ordering (`orderBy: null` passed to `PaginateAsync`).
- XML doc comments on every new public type and member, citing the use case and FR numbers, matching the density of the surrounding code.
- Commit messages: lowercase Conventional Commits subject, ≤50 chars, imperative, body wrapped at 72.
- `dotnet build src/ArturRios.Heimdall.sln` must be clean before any commit.

## Notes for the implementer

**Where the pipeline already refuses callers.** `ActorLivenessFilter` runs as a global authorization filter and answers `401` when a bearer token names a person who does not exist or is logically deleted, or whose role claim has drifted. So a handler's own "no live person" branch is defence in depth and is reachable in unit tests but not through HTTP. A *Google User*, by contrast, passes that filter (it checks the `GoogleUser` table too) and does reach the handler — which is why the `403 NotEligible` path in Task 1 has a real functional test and the "unknown person" path does not.

**Two identity tables, one token shape.** `Person` and `GoogleUser` have separate `PublicId` spaces. A Google-issued token's subject names a `GoogleUser`, so a lookup against `Person` by `ActingPersonId` misses — this is exactly how `EnableTwoFactorAuthCommandHandler` detects a Google User, and the same technique is used in Task 1.

---

### Task 1: `GET /api/auth/2fa` — the caller's own two-factor status

**Files:**
- Create: `src/Application/ArturRios.Heimdall.Query/Input/GetTwoFactorStatusQuery.cs`
- Create: `src/Application/ArturRios.Heimdall.Query/Output/TwoFactorStatusOutput.cs`
- Create: `src/Application/ArturRios.Heimdall.Query/Handlers/GetTwoFactorStatusQueryHandler.cs`
- Modify: `src/Application/ArturRios.Heimdall.Shared/Messages/TwoFactorMessages.cs`
- Modify: `src/Application/ArturRios.Heimdall.Shared/Messages/TwoFactorMessageMap.cs`
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs:206-233` (query handler registrations)
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/AuthController.cs:15` (constructor) and end of class
- Test: `tests/Application/ArturRios.Heimdall.Query.Tests/GetTwoFactorStatusQueryHandlerTests.cs`
- Test: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/AuthControllerGetTwoFactorStatusTests.cs`

**Interfaces:**
- Consumes: `IActorScoped` (`ArturRios.Heimdall.Shared.Security`); `IAsyncReadOnlyRepository<T>` (`ArturRios.Data.Relational.Core.Interfaces`); entities `Person`, `TwoFactorAuth`, `TwoFactorRecoveryCode`; `TwoFactorMessages.NotEligible`.
- Produces: `GetTwoFactorStatusQuery`, `TwoFactorStatusOutput` (`bool IsActive`, `bool AppEnabled`, `bool EmailEnabled`, `int RemainingRecoveryCodes`), `GetTwoFactorStatusQueryHandler`, `TwoFactorMessages.StatusRetrieved`. Task 5 documents the endpoint; nothing else depends on this task.

- [ ] **Step 1: Write the failing unit tests**

Create `tests/Application/ArturRios.Heimdall.Query.Tests/GetTwoFactorStatusQueryHandlerTests.cs`:

```csharp
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for GetTwoFactorStatusQueryHandler (UC-36 – UC-40, FR-2F-15): the caller's own
// two-factor state. Covers no configuration (200, all false), a pending setup, an active
// configuration, the unused-recovery-code count, and a caller who is not an eligible person
// (AF-36b's NotEligible — a Google User, or a token naming no live person).
public class GetTwoFactorStatusQueryHandlerTests
{
    private static Person PersonWith(Guid publicId) => new()
    {
        Id = 1,
        PublicId = publicId,
        Name = "Ana",
        Email = "ana@test.local",
        RoleId = (long)Roles.User
    };

    private static async Task<AsyncFakeRepository<T>> RepositoryWith<T>(params T[] items) where T : class
    {
        var repository = new AsyncFakeRepository<T>();

        foreach (var item in items)
        {
            await repository.CreateAsync(item);
        }

        return repository;
    }

    private static GetTwoFactorStatusQuery QueryFor(Guid personId) => new()
    {
        ActingPersonId = personId,
        ActingRole = (int)Roles.User
    };

    [UnitFact]
    public async Task GivenNoConfiguration_WhenHandlingGetTwoFactorStatus_ThenReturnsAllFalse()
    {
        // Given a live person who never enabled two-factor authentication
        var personId = Guid.NewGuid();
        var persons = await RepositoryWith(PersonWith(personId));
        var configurations = await RepositoryWith<TwoFactorAuth>();
        var recoveryCodes = await RepositoryWith<TwoFactorRecoveryCode>();
        var handler = new GetTwoFactorStatusQueryHandler(persons, configurations, recoveryCodes);

        // When
        var output = await handler.HandleAsync(QueryFor(personId));

        // Then — the ordinary "never turned it on" state is a success, not a refusal
        Assert.True(output.Success);
        Assert.False(output.Data!.IsActive);
        Assert.False(output.Data.AppEnabled);
        Assert.False(output.Data.EmailEnabled);
        Assert.Equal(0, output.Data.RemainingRecoveryCodes);
        Assert.Contains(TwoFactorMessages.StatusRetrieved, output.Messages);
    }

    [UnitFact]
    public async Task GivenPendingSetup_WhenHandlingGetTwoFactorStatus_ThenReportsMethodsButNotActive()
    {
        // Given UC-36 initiated setup for both methods and UC-37 has not confirmed it
        var personId = Guid.NewGuid();
        var persons = await RepositoryWith(PersonWith(personId));
        var configurations = await RepositoryWith(new TwoFactorAuth
        {
            Id = 1, PersonId = 1, AppEnabled = true, EmailEnabled = true, IsActive = false
        });
        var recoveryCodes = await RepositoryWith<TwoFactorRecoveryCode>();
        var handler = new GetTwoFactorStatusQueryHandler(persons, configurations, recoveryCodes);

        // When
        var output = await handler.HandleAsync(QueryFor(personId));

        // Then — pending is representable as !IsActive with methods set
        Assert.True(output.Success);
        Assert.False(output.Data!.IsActive);
        Assert.True(output.Data.AppEnabled);
        Assert.True(output.Data.EmailEnabled);
    }

    [UnitFact]
    public async Task GivenActiveConfiguration_WhenHandlingGetTwoFactorStatus_ThenReportsItsMethods()
    {
        // Given an active app-only configuration
        var personId = Guid.NewGuid();
        var persons = await RepositoryWith(PersonWith(personId));
        var configurations = await RepositoryWith(new TwoFactorAuth
        {
            Id = 1, PersonId = 1, AppEnabled = true, EmailEnabled = false, IsActive = true
        });
        var recoveryCodes = await RepositoryWith<TwoFactorRecoveryCode>();
        var handler = new GetTwoFactorStatusQueryHandler(persons, configurations, recoveryCodes);

        // When
        var output = await handler.HandleAsync(QueryFor(personId));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.IsActive);
        Assert.True(output.Data.AppEnabled);
        Assert.False(output.Data.EmailEnabled);
    }

    [UnitFact]
    public async Task GivenUsedAndUnusedRecoveryCodes_WhenHandlingGetTwoFactorStatus_ThenCountsOnlyUnused()
    {
        // Given three codes for this configuration, one consumed, plus one for another configuration
        var personId = Guid.NewGuid();
        var persons = await RepositoryWith(PersonWith(personId));
        var configurations = await RepositoryWith(new TwoFactorAuth
        {
            Id = 1, PersonId = 1, AppEnabled = true, IsActive = true
        });
        var recoveryCodes = await RepositoryWith(
            new TwoFactorRecoveryCode { Id = 1, TwoFactorAuthId = 1, CodeHash = [1], Used = false },
            new TwoFactorRecoveryCode { Id = 2, TwoFactorAuthId = 1, CodeHash = [2], Used = false },
            new TwoFactorRecoveryCode { Id = 3, TwoFactorAuthId = 1, CodeHash = [3], Used = true },
            new TwoFactorRecoveryCode { Id = 4, TwoFactorAuthId = 2, CodeHash = [4], Used = false });
        var handler = new GetTwoFactorStatusQueryHandler(persons, configurations, recoveryCodes);

        // When
        var output = await handler.HandleAsync(QueryFor(personId));

        // Then — two unused, belonging to this configuration only
        Assert.Equal(2, output.Data!.RemainingRecoveryCodes);
    }

    [UnitFact]
    public async Task GivenCallerIsNotAPerson_WhenHandlingGetTwoFactorStatus_ThenReturnsNotEligible()
    {
        // Given a token naming no live Person — a Google User (separate PublicId space), or a
        // person since removed. AF-36b treats both alike.
        var persons = await RepositoryWith(PersonWith(Guid.NewGuid()));
        var configurations = await RepositoryWith<TwoFactorAuth>();
        var recoveryCodes = await RepositoryWith<TwoFactorRecoveryCode>();
        var handler = new GetTwoFactorStatusQueryHandler(persons, configurations, recoveryCodes);

        // When
        var output = await handler.HandleAsync(QueryFor(Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Null(output.Data);
        Assert.Contains(TwoFactorMessages.NotEligible, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedPerson_WhenHandlingGetTwoFactorStatus_ThenReturnsNotEligible()
    {
        // Given a logically deleted person — the lookup excludes them, as every actor lookup does
        var personId = Guid.NewGuid();
        var person = PersonWith(personId);
        person.IsDeleted = true;
        var persons = await RepositoryWith(person);
        var configurations = await RepositoryWith<TwoFactorAuth>();
        var recoveryCodes = await RepositoryWith<TwoFactorRecoveryCode>();
        var handler = new GetTwoFactorStatusQueryHandler(persons, configurations, recoveryCodes);

        // When
        var output = await handler.HandleAsync(QueryFor(personId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.NotEligible, output.Errors);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Application/ArturRios.Heimdall.Query.Tests --filter "FullyQualifiedName~GetTwoFactorStatusQueryHandlerTests"
```

Expected: compilation failure — `GetTwoFactorStatusQueryHandler`, `GetTwoFactorStatusQuery`, and `TwoFactorMessages.StatusRetrieved` do not exist.

- [ ] **Step 3: Add the message and its status mapping**

In `src/Application/ArturRios.Heimdall.Shared/Messages/TwoFactorMessages.cs`, append inside the class:

```csharp
    /// <summary>
    ///     FR-2F-15 main flow: the caller's own two-factor status was read. Returned whether or not
    ///     any configuration exists — "never enabled" is the ordinary state of most accounts and is
    ///     reported as a success with every flag false, not as a refusal.
    /// </summary>
    public const string StatusRetrieved = "Two-factor authentication status retrieved.";
```

In `src/Application/ArturRios.Heimdall.Shared/Messages/TwoFactorMessageMap.cs`, add to the dictionary:

```csharp
            // FR-2F-15 main flow — status read, including the all-false "never enabled" state.
            [TwoFactorMessages.StatusRetrieved] = HttpStatusCodes.Ok,
```

- [ ] **Step 4: Create the query type**

Create `src/Application/ArturRios.Heimdall.Query/Input/GetTwoFactorStatusQuery.cs`:

```csharp
using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to read the caller's own two-factor authentication status (FR-2F-15). The person
///     acted on is always the caller: <see cref="ActingPersonId" />/<see cref="ActingRole" /> are
///     set by the controller from the authenticated caller and are never taken from the request,
///     which is what keeps a person's configuration reachable only through their own identity and
///     never by an identifier in a path (see <c>TwoFactorAuth</c>). The pagination members inherited
///     from <see cref="BaseQuery" /> are unused.
/// </summary>
public class GetTwoFactorStatusQuery : BaseQuery, IActorScoped
{
    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
```

- [ ] **Step 5: Create the output type**

Create `src/Application/ArturRios.Heimdall.Query/Output/TwoFactorStatusOutput.cs`:

```csharp
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     The caller's own two-factor authentication state (FR-2F-15). Carries no secret: never the
///     TOTP secret, never a recovery code, only how many recovery codes remain unspent.
/// </summary>
/// <remarks>
///     A setup initiated by UC-36 but not yet confirmed by UC-37 needs no field of its own — it is
///     exactly <c>!IsActive &amp;&amp; (AppEnabled || EmailEnabled)</c>, since a configuration row
///     only ever exists because setup was initiated. A caller who never initiated setup gets every
///     flag <c>false</c> and <see cref="RemainingRecoveryCodes" /> zero.
/// </remarks>
public class TwoFactorStatusOutput : QueryOutput
{
    /// <summary>Whether two-factor authentication is confirmed and in force (FR-2F-04).</summary>
    public bool IsActive { get; set; }

    /// <summary>Whether the authenticator-app method is configured (FR-2F-02).</summary>
    public bool AppEnabled { get; set; }

    /// <summary>Whether the email method is configured (FR-2F-03).</summary>
    public bool EmailEnabled { get; set; }

    /// <summary>How many issued recovery codes remain unused (FR-2F-05, FR-2F-06).</summary>
    public int RemainingRecoveryCodes { get; set; }
}
```

- [ ] **Step 6: Create the handler**

Create `src/Application/ArturRios.Heimdall.Query/Handlers/GetTwoFactorStatusQueryHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="GetTwoFactorStatusQuery" /> (FR-2F-15): reports the caller's own two-factor
///     state — active or not, which methods are configured, and how many recovery codes remain.
/// </summary>
/// <remarks>
///     <para>
///         A caller who never enabled two-factor authentication is answered with every flag
///         <c>false</c> and a zero count, not with a refusal. That is the ordinary state of most
///         accounts, and a client's settings screen should not have to render its most common state
///         out of an error branch. <c>NotActive</c>'s 404 stays what UC-39 and UC-40 use it for:
///         refusing an operation that requires an active configuration.
///     </para>
///     <para>
///         A caller who is not an eligible person is refused with <c>NotEligible</c> (403), exactly
///         as UC-36's AF-36b refuses the same caller. <see cref="GoogleUser" /> and
///         <see cref="Person" /> are separate tables with separate <c>PublicId</c> spaces, so a
///         Google-issued token's subject never resolves here — and FR-2F-01 makes Google Users
///         permanently ineligible, which an all-false success would misreport as "off, and you could
///         turn it on". The same miss covers a token naming a person who no longer exists;
///         <c>ActorLivenessFilter</c> already answers 401 for that case before a request arrives, so
///         this branch is defence in depth.
///     </para>
/// </remarks>
public class GetTwoFactorStatusQueryHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorReader,
    IAsyncReadOnlyRepository<TwoFactorRecoveryCode> recoveryCodeReader)
    : IQueryHandlerAsync<GetTwoFactorStatusQuery, TwoFactorStatusOutput>
{
    public async Task<DataOutput<TwoFactorStatusOutput?>> HandleAsync(GetTwoFactorStatusQuery query)
    {
        var output = DataOutput<TwoFactorStatusOutput?>.New;

        // AF-36b: the caller must be a live person. A Google User misses this lookup entirely.
        var personId = await personReader.Query()
            .Where(person => person.PublicId == query.ActingPersonId && !person.IsDeleted)
            .Select(person => (long?)person.Id)
            .FirstOrDefaultAsync();

        if (personId is null)
        {
            return output.WithError(TwoFactorMessages.NotEligible);
        }

        var configuration = await twoFactorReader.Query()
            .FirstOrDefaultAsync(x => x.PersonId == personId.Value);

        if (configuration is null)
        {
            return output
                .WithData(new TwoFactorStatusOutput())
                .WithMessage(TwoFactorMessages.StatusRetrieved);
        }

        var remainingRecoveryCodes = await recoveryCodeReader.Query()
            .CountAsync(code => code.TwoFactorAuthId == configuration.Id && !code.Used);

        return output
            .WithData(new TwoFactorStatusOutput
            {
                IsActive = configuration.IsActive,
                AppEnabled = configuration.AppEnabled,
                EmailEnabled = configuration.EmailEnabled,
                RemainingRecoveryCodes = remainingRecoveryCodes
            })
            .WithMessage(TwoFactorMessages.StatusRetrieved);
    }
}
```

- [ ] **Step 7: Run the unit tests to verify they pass**

```bash
dotnet test tests/Application/ArturRios.Heimdall.Query.Tests --filter "FullyQualifiedName~GetTwoFactorStatusQueryHandlerTests"
```

Expected: PASS, 6 tests.

- [ ] **Step 8: Register the handler**

In `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`, after the `ListScopeGoogleUsersQueryHandler` registration (around line 233), add:

```csharp
        Builder.Services
            .AddScoped<IQueryHandlerAsync<GetTwoFactorStatusQuery, TwoFactorStatusOutput>,
                GetTwoFactorStatusQueryHandler>();
```

- [ ] **Step 9: Add the controller action**

In `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/AuthController.cs`, change the class declaration to take a query mediator as well:

```csharp
public class AuthController(CommandMediator commandMediator, QueryMediator queryMediator) : Controller
```

Add these usings if absent: `ArturRios.Heimdall.Query.Input;`, `ArturRios.Heimdall.Query.Output;`, `ArturRios.Mediator.Query;`.

Append the action at the end of the class:

```csharp
    /// <summary>
    ///     Reports the caller's own two-factor authentication status (FR-2F-15): whether it is
    ///     active, which methods are configured, and how many recovery codes remain unused. The
    ///     person read is always the caller themselves — taken from the bearer token, the same as
    ///     <see cref="EnableTwoFactorAuth" /> — so a configuration is never addressed by an
    ///     identifier in a path.
    /// </summary>
    /// <remarks>
    ///     No <c>RoleRequirement</c>, for the same reason its <c>POST</c> siblings have none: the
    ///     authorization matrix grants two-factor management to all three person roles and withholds
    ///     it from anonymous callers, which authentication alone enforces. A caller with no
    ///     configuration is answered 200 with every flag false; a Google User is answered 403, since
    ///     FR-2F-01 makes them permanently ineligible.
    /// </remarks>
    [HttpGet("2fa")]
    public async Task<ActionResult<DataOutput<TwoFactorStatusOutput?>>> GetTwoFactorStatus()
    {
        var query = new GetTwoFactorStatusQuery();
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecuteQueryAsync<GetTwoFactorStatusQuery, TwoFactorStatusOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: TwoFactorMessageMap.StatusCodes);
    }
```

- [ ] **Step 10: Write the failing functional tests**

Create `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/AuthControllerGetTwoFactorStatusTests.cs`. Model the fixture usage on `AuthControllerEnableTwoFactorAuthTests.cs` — read that file first for the exact seeding helpers and Google-token minting it uses, and reuse them rather than inventing new ones.

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET /api/auth/2fa (FR-2F-15): the caller's own status over the real
// pipeline. Covers no configuration (200, all false), a pending setup, an active configuration
// with its unused recovery-code count, a Google User (403, AF-36b), and 401 unauthenticated.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerGetTwoFactorStatusTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private async Task<Person> SeedPersonAsync()
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Ana",
            Email = $"ana-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task SeedTwoFactorAsync(
        Person person, bool isActive, bool appEnabled, bool emailEnabled, int unusedCodes, int usedCodes)
    {
        await using var context = db.CreateContext();
        var configuration = new TwoFactorAuth
        {
            PersonId = person.Id,
            AppEnabled = appEnabled,
            EmailEnabled = emailEnabled,
            IsActive = isActive
        };
        context.TwoFactorAuths.Add(configuration);
        await context.SaveChangesAsync();

        for (var i = 0; i < unusedCodes + usedCodes; i++)
        {
            context.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCode
            {
                TwoFactorAuthId = configuration.Id,
                CodeHash = [(byte)i],
                Used = i >= unusedCodes
            });
        }

        await context.SaveChangesAsync();
    }

    [FunctionalFact]
    public async Task GivenNoConfiguration_WhenGetTwoFactorStatus_ThenReturnsOkWithAllFalse()
    {
        // Given a person who never enabled two-factor authentication
        var person = await SeedPersonAsync();
        Authorize(TestTokens.For(person.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<TwoFactorStatusOutput?>>("/api/auth/2fa");

        // Then — a success, not a 404
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body?.Data?.IsActive);
        Assert.False(response.Body?.Data?.AppEnabled);
        Assert.False(response.Body?.Data?.EmailEnabled);
        Assert.Equal(0, response.Body?.Data?.RemainingRecoveryCodes);
    }

    [FunctionalFact]
    public async Task GivenPendingSetup_WhenGetTwoFactorStatus_ThenReportsMethodsButNotActive()
    {
        // Given UC-36 initiated an app-method setup that UC-37 has not confirmed
        var person = await SeedPersonAsync();
        await SeedTwoFactorAsync(person, isActive: false, appEnabled: true, emailEnabled: false,
            unusedCodes: 0, usedCodes: 0);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<TwoFactorStatusOutput?>>("/api/auth/2fa");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body?.Data?.IsActive);
        Assert.True(response.Body?.Data?.AppEnabled);
    }

    [FunctionalFact]
    public async Task GivenActiveConfiguration_WhenGetTwoFactorStatus_ThenCountsOnlyUnusedRecoveryCodes()
    {
        // Given an active configuration with ten codes, three of them already spent
        var person = await SeedPersonAsync();
        await SeedTwoFactorAsync(person, isActive: true, appEnabled: true, emailEnabled: true,
            unusedCodes: 7, usedCodes: 3);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<TwoFactorStatusOutput?>>("/api/auth/2fa");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.IsActive);
        Assert.True(response.Body?.Data?.AppEnabled);
        Assert.True(response.Body?.Data?.EmailEnabled);
        Assert.Equal(7, response.Body?.Data?.RemainingRecoveryCodes);
    }

    [FunctionalFact]
    public async Task GivenUnauthenticatedCaller_WhenGetTwoFactorStatus_ThenReturnsUnauthorized()
    {
        // Given no bearer token
        // When
        var response = await Gateway.GetAsync<DataOutput<TwoFactorStatusOutput?>>("/api/auth/2fa");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

Then add the Google User case. `AuthControllerEnableTwoFactorAuthTests` already proves AF-36b for a Google User over HTTP — copy its seeding helper and its token minting verbatim into this file and add:

```csharp
    [FunctionalFact]
    public async Task GivenGoogleUser_WhenGetTwoFactorStatus_ThenReturnsForbidden()
    {
        // Given a live Google User's token — GoogleUser and Person are separate PublicId spaces,
        // so the person lookup misses and FR-2F-01 refuses them (AF-36b)
        var googleUser = await SeedGoogleUserAsync();
        Authorize(TestTokens.For(googleUser.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.GetAsync<DataOutput<TwoFactorStatusOutput?>>("/api/auth/2fa");

        // Then — 403, not an all-false 200 that would imply they could turn it on
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(TwoFactorMessages.NotEligible, response.Body!.Errors);
    }
```

If `AuthControllerEnableTwoFactorAuthTests` names its helper something other than `SeedGoogleUserAsync`, use that name and drop the helper you copied.

- [ ] **Step 11: Build and run the full affected suites**

```bash
dotnet build src/ArturRios.Heimdall.sln
```

Expected: no errors.

```bash
dotnet test tests/Application/ArturRios.Heimdall.Query.Tests --filter "FullyQualifiedName~GetTwoFactorStatusQueryHandlerTests"
```

Expected: PASS, 6 tests.

```bash
dotnet test tests/Presentation/ArturRios.Heimdall.WebApi.Tests --filter "FullyQualifiedName~AuthControllerGetTwoFactorStatusTests"
```

Expected: PASS, 5 tests. (Functional tests need the PostgreSQL fixture — see `docs/content/en/docs/testing.md` if it does not start.)

- [ ] **Step 12: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Query src/Application/ArturRios.Heimdall.Shared/Messages src/Presentation/ArturRios.Heimdall.WebApi tests/Application/ArturRios.Heimdall.Query.Tests/GetTwoFactorStatusQueryHandlerTests.cs tests/Presentation/ArturRios.Heimdall.WebApi.Tests/AuthControllerGetTwoFactorStatusTests.cs
git commit -m "feat: report the caller's two-factor status"
```

---

### Task 2: `twoFactorEnabled` on `PersonOutput`

**Files:**
- Modify: `src/Application/ArturRios.Heimdall.Query/Output/PersonOutput.cs`
- Modify: `src/Application/ArturRios.Heimdall.Query/Handlers/GetPersonByIdQueryHandler.cs:52-64` (the `PersonOutput` projection)
- Modify: `src/Application/ArturRios.Heimdall.Query/Handlers/ListScopePersonsQueryHandler.cs` (the `projected` select)
- Modify: `src/Application/ArturRios.Heimdall.Query/Handlers/ListScopeOwnersQueryHandler.cs` (the `projected` select)
- Test: `tests/Application/ArturRios.Heimdall.Query.Tests/GetPersonByIdQueryHandlerTests.cs` (add cases)
- Test: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/PersonControllerGetByIdTests.cs` (add a case)

**Interfaces:**
- Consumes: `Person.TwoFactorAuth` navigation (`src/Domain/ArturRios.Heimdall.Domain/Entities/Person.cs:117`), nullable.
- Produces: `PersonOutput.TwoFactorEnabled` (`bool`). Task 3 must *not* surface this field — that is the whole reason `PersonSummaryOutput` exists.

- [ ] **Step 1: Write the failing unit tests**

Append these three tests to `tests/Application/ArturRios.Heimdall.Query.Tests/GetPersonByIdQueryHandlerTests.cs`, inside the existing class. They use that file's own `Scope`, `User`, and `RepositoryWith` helpers unchanged.

```csharp
    [UnitFact]
    public async Task GivenPersonWithActiveTwoFactor_WhenHandlingGetPersonById_ThenTwoFactorEnabledIsTrue()
    {
        // Given a person whose two-factor configuration UC-37 has confirmed
        var scope = Scope(1);
        var target = User(10, scope);
        target.TwoFactorAuth = new TwoFactorAuth
        {
            Id = 1, PersonId = target.Id, AppEnabled = true, IsActive = true
        };
        var repository = await RepositoryWith(target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When the person reads themselves
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = target.PublicId, ActingRole = (int)Roles.User
        });

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.TwoFactorEnabled);
    }

    [UnitFact]
    public async Task GivenPersonWithPendingTwoFactor_WhenHandlingGetPersonById_ThenTwoFactorEnabledIsFalse()
    {
        // Given a configuration row UC-36 created and UC-37 has not confirmed
        var scope = Scope(1);
        var target = User(10, scope);
        target.TwoFactorAuth = new TwoFactorAuth
        {
            Id = 1, PersonId = target.Id, AppEnabled = true, IsActive = false
        };
        var repository = await RepositoryWith(target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When the person reads themselves
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = target.PublicId, ActingRole = (int)Roles.User
        });

        // Then — the flag tracks IsActive, not the mere existence of a row
        Assert.True(output.Success);
        Assert.False(output.Data!.TwoFactorEnabled);
    }

    [UnitFact]
    public async Task GivenPersonWithNoTwoFactor_WhenHandlingGetPersonById_ThenTwoFactorEnabledIsFalse()
    {
        // Given a person who never enabled two-factor authentication
        var scope = Scope(1);
        var target = User(10, scope);
        var repository = await RepositoryWith(target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When the person reads themselves
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = target.PublicId, ActingRole = (int)Roles.User
        });

        // Then
        Assert.True(output.Success);
        Assert.False(output.Data!.TwoFactorEnabled);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Application/ArturRios.Heimdall.Query.Tests --filter "FullyQualifiedName~GetPersonByIdQueryHandlerTests"
```

Expected: compilation failure — `PersonOutput.TwoFactorEnabled` does not exist.

- [ ] **Step 3: Add the field**

In `src/Application/ArturRios.Heimdall.Query/Output/PersonOutput.cs`, add after `EmailVerified`:

```csharp
    /// <summary>
    ///     Whether the person has an active two-factor authentication configuration (FR-2F-15). The
    ///     configured methods are deliberately not published here — an administrator reading a
    ///     listing learns whether an account is protected, which is what makes coverage visible,
    ///     but not how, which would otherwise map out which accounts fall back to email.
    ///     <c>GET /api/auth/2fa</c> is where a person reads the detail, and only for themselves.
    /// </summary>
    public bool TwoFactorEnabled { get; set; }
```

- [ ] **Step 4: Add the projection to all three handlers**

In each of `GetPersonByIdQueryHandler`, `ListScopePersonsQueryHandler`, and `ListScopeOwnersQueryHandler`, inside the `new PersonOutput { ... }` initializer, add after the `EmailVerified` line:

```csharp
                    TwoFactorEnabled = x.TwoFactorAuth != null && x.TwoFactorAuth.IsActive,
```

Match the surrounding indentation in each file — it is deeper in `GetPersonByIdQueryHandler` (nested inside `PersonProjection`) than in the two list handlers.

- [ ] **Step 5: Run the unit tests to verify they pass**

```bash
dotnet test tests/Application/ArturRios.Heimdall.Query.Tests
```

Expected: PASS, whole project. If `ListScopePersonsQueryHandlerTests` or `ListScopeOwnersQueryHandlerTests` fail on a null-reference through the navigation, the fake repository is not lazy-loading — set `TwoFactorAuth` explicitly to `null` in those tests' person factories rather than changing the projection.

- [ ] **Step 6: Add the functional assertion**

In `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/PersonControllerGetByIdTests.cs`, add one test proving the field travels over the wire. Reuse the file's existing person-seeding helper, then seed a confirmed configuration for that person and assert:

```csharp
        Assert.True(response.Body?.Data?.TwoFactorEnabled);
```

and, in the file's existing main-flow test for a person with no configuration, assert:

```csharp
        Assert.False(response.Body?.Data?.TwoFactorEnabled);
```

- [ ] **Step 7: Run the affected functional tests**

```bash
dotnet test tests/Presentation/ArturRios.Heimdall.WebApi.Tests --filter "FullyQualifiedName~PersonController"
```

Expected: PASS. Any failure here is an expected-payload assertion that needs the new field added — fix those rather than removing the field.

- [ ] **Step 8: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Query tests/Application/ArturRios.Heimdall.Query.Tests tests/Presentation/ArturRios.Heimdall.WebApi.Tests
git commit -m "feat: publish two-factor coverage on person reads"
```

---

### Task 3: `GET /api/persons/scope-admins`

**Files:**
- Create: `src/Application/ArturRios.Heimdall.Query/Input/ListScopeAdminsQuery.cs`
- Create: `src/Application/ArturRios.Heimdall.Query/Input/Validation/ListScopeAdminsQueryValidator.cs`
- Create: `src/Application/ArturRios.Heimdall.Query/Output/PersonSummaryOutput.cs`
- Create: `src/Application/ArturRios.Heimdall.Query/Handlers/ListScopeAdminsQueryHandler.cs`
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs:206-245` (handler and validator registrations)
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/PersonController.cs` (append action after `ListScopeOwners`)
- Test: `tests/Application/ArturRios.Heimdall.Query.Tests/ListScopeAdminsQueryHandlerTests.cs`
- Test: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/PersonControllerListScopeAdminsTests.cs`

**Interfaces:**
- Consumes: `IScopeOwnershipChecker.ActorMayManageScopeAsync(int actingRole, Guid actingPersonId, long scopeId)`; `PersonMessages.ScopeNotFound`, `PersonMessages.NotScopeOwner`, `PersonMessages.PersonsRetrievedSuccessfully`; `PaginatedQueryValidator<TQuery>`; `PaginationMessages.FilterTooLong`.
- Produces: `ListScopeAdminsQuery` (`string? Name`, `string? Email`, `Guid? ExcludeOwnersOfScopeId`), `PersonSummaryOutput` (`Guid Id`, `string Name`, `string Email`), `ListScopeAdminsQueryHandler`, `ListScopeAdminsQueryValidator`.

- [ ] **Step 1: Write the failing unit tests**

Create `tests/Application/ArturRios.Heimdall.Query.Tests/ListScopeAdminsQueryHandlerTests.cs`:

```csharp
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for ListScopeAdminsQueryHandler (UC-07 read d, FR-PE-12): every live ScopeAdmin,
// paginated and filterable, projected to three fields. Covers the role filter, deleted exclusion,
// name/email filters, the excludeOwnersOfScopeId exclusion and its ownership gate (an unknown
// scope → ScopeNotFound, a non-owning Scope Admin → NotScopeOwner, a System Admin bypassing),
// exclusion before pagination, and the ordering tiebreaker.
public class ListScopeAdminsQueryHandlerTests
{
    private static Scope Scope(long id, bool isDeleted = false) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}", IsDeleted = isDeleted };

    private static Person Admin(long id, string name, string email, bool isDeleted = false, Scope? owns = null)
    {
        var person = new Person
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            Name = name,
            Email = email,
            RoleId = (long)Roles.ScopeAdmin,
            IsDeleted = isDeleted
        };

        if (owns is not null)
        {
            person.ScopeOwnerships = [new ScopeOwner { ScopeId = owns.Id, Scope = owns }];
        }

        return person;
    }

    private static Person NonAdmin(long id, Roles role) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"person-{id}",
        Email = $"person-{id}@test.local",
        RoleId = (long)role
    };

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);

        return checker.Object;
    }

    private static async Task<AsyncFakeRepository<T>> RepositoryWith<T>(params T[] items) where T : class
    {
        var repository = new AsyncFakeRepository<T>();

        foreach (var item in items)
        {
            await repository.CreateAsync(item);
        }

        return repository;
    }

    private static ListScopeAdminsQuery Query(Guid? excludeOwnersOfScopeId = null, int pageSize = 10) => new()
    {
        PageNumber = 1,
        PageSize = pageSize,
        ExcludeOwnersOfScopeId = excludeOwnersOfScopeId,
        ActingPersonId = Guid.NewGuid(),
        ActingRole = (int)Roles.SystemAdmin
    };

    private static ListScopeAdminsQueryHandler HandlerFor(
        AsyncFakeRepository<Scope> scopes, AsyncFakeRepository<Person> persons, bool ownershipAllowed = true) =>
        new(scopes, persons, Ownership(ownershipAllowed), new ListScopeAdminsQueryValidator());

    [UnitFact]
    public async Task GivenMixedRoles_WhenHandlingListScopeAdmins_ThenOnlyScopeAdminsAreReturned()
    {
        // Given two Scope Admins, one User, and one System Admin
        var ana = Admin(10, "Ana", "ana@test.local");
        var bruno = Admin(11, "Bruno", "bruno@test.local");
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(ana, bruno, NonAdmin(12, Roles.User), NonAdmin(13, Roles.SystemAdmin));
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([ana.PublicId, bruno.PublicId], output.Data!.Select(x => x.Id));
        Assert.Contains(PersonMessages.PersonsRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenDeletedScopeAdmin_WhenHandlingListScopeAdmins_ThenItIsExcluded()
    {
        // Given one live and one logically deleted Scope Admin — a deleted admin is never a valid
        // owner, so offering them in a picker could only produce a failed submission
        var ana = Admin(10, "Ana", "ana@test.local");
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(ana, Admin(11, "Bruno", "bruno@test.local", isDeleted: true));
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal([ana.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenNameFilter_WhenHandlingListScopeAdmins_ThenMatchesCaseInsensitiveSubstring()
    {
        // Given admins whose names differ in case
        var ana = Admin(10, "Ana Silva", "ana@test.local");
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(ana, Admin(11, "Bruno", "bruno@test.local"));
        var handler = HandlerFor(scopes, persons);
        var query = Query();
        query.Name = "SILV";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal([ana.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenEmailFilter_WhenHandlingListScopeAdmins_ThenMatchesCaseInsensitiveSubstring()
    {
        // Given admins on different domains
        var ana = Admin(10, "Ana", "ana@heimdall.test");
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(ana, Admin(11, "Bruno", "bruno@other.test"));
        var handler = HandlerFor(scopes, persons);
        var query = Query();
        query.Email = "HEIMDALL";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal([ana.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenExcludeOwnersOfScope_WhenHandlingListScopeAdmins_ThenCurrentOwnersAreRemoved()
    {
        // Given a scope whose owner is one of three admins (UI-14 AF-14c)
        var scope = Scope(1);
        var owner = Admin(10, "Ana", "ana@test.local", owns: scope);
        var bruno = Admin(11, "Bruno", "bruno@test.local");
        var carla = Admin(12, "Carla", "carla@test.local");
        var scopes = await RepositoryWith(scope);
        var persons = await RepositoryWith(owner, bruno, carla);
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query(excludeOwnersOfScopeId: scope.PublicId));

        // Then
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([bruno.PublicId, carla.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenExcludedOwnerAndFullPage_WhenHandlingListScopeAdmins_ThenPageIsNotShortened()
    {
        // Given four admins, one of them already an owner, and a page size of three: the exclusion
        // must happen before pagination, or the page comes back with two
        var scope = Scope(1);
        var scopes = await RepositoryWith(scope);
        var persons = await RepositoryWith(
            Admin(10, "Ana", "ana@test.local", owns: scope),
            Admin(11, "Bruno", "bruno@test.local"),
            Admin(12, "Carla", "carla@test.local"),
            Admin(13, "Diego", "diego@test.local"));
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query(excludeOwnersOfScopeId: scope.PublicId, pageSize: 3));

        // Then
        Assert.Equal(3, output.TotalItems);
        Assert.Equal(3, output.Data!.Count());
    }

    [UnitFact]
    public async Task GivenUnknownScopeToExclude_WhenHandlingListScopeAdmins_ThenReturnsScopeNotFound()
    {
        // Given no such scope
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(Admin(10, "Ana", "ana@test.local"));
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query(excludeOwnersOfScopeId: Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedScopeToExclude_WhenHandlingListScopeAdmins_ThenReturnsScopeNotFound()
    {
        // Given a logically deleted scope
        var scope = Scope(1, isDeleted: true);
        var scopes = await RepositoryWith(scope);
        var persons = await RepositoryWith(Admin(10, "Ana", "ana@test.local"));
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query(excludeOwnersOfScopeId: scope.PublicId));

        // Then
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenActorNotOwningExcludedScope_WhenHandlingListScopeAdmins_ThenReturnsNotScopeOwner()
    {
        // Given a Scope Admin naming a scope they do not own. Without this gate, running the query
        // with and without the parameter and diffing enumerates any scope's owners.
        var scope = Scope(1);
        var scopes = await RepositoryWith(scope);
        var persons = await RepositoryWith(Admin(10, "Ana", "ana@test.local", owns: scope));
        var handler = HandlerFor(scopes, persons, ownershipAllowed: false);

        // When
        var output = await handler.HandleAsync(Query(excludeOwnersOfScopeId: scope.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenNoScopeToExclude_WhenHandlingListScopeAdmins_ThenOwnershipIsNotChecked()
    {
        // Given a caller passing no excludeOwnersOfScopeId (UI-11, where no scope exists yet):
        // the unfiltered listing is open to both administrator roles
        var ana = Admin(10, "Ana", "ana@test.local");
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(ana);
        var handler = HandlerFor(scopes, persons, ownershipAllowed: false);
        var query = Query();
        query.ActingRole = (int)Roles.ScopeAdmin;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.True(output.Success);
        Assert.Equal([ana.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenAdminsSharingAName_WhenHandlingListScopeAdmins_ThenOrderIsDeterministic()
    {
        // Given two admins with the same name: the identifier tiebreaker is what stops one of them
        // appearing on two pages while the other appears on none
        var first = Admin(10, "Ana Silva", "ana1@test.local");
        var second = Admin(11, "Ana Silva", "ana2@test.local");
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(second, first);
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        var expected = new[] { first.PublicId, second.PublicId }.OrderBy(x => x);
        Assert.Equal(expected, output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenPageSizeAboveTheBound_WhenHandlingListScopeAdmins_ThenReturnsValidationError()
    {
        // Given NFR-10's page-size bound exceeded
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(Admin(10, "Ana", "ana@test.local"));
        var handler = HandlerFor(scopes, persons);
        var query = Query(pageSize: 500);

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PaginationMessages.InvalidPageSize, output.Errors);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Application/ArturRios.Heimdall.Query.Tests --filter "FullyQualifiedName~ListScopeAdminsQueryHandlerTests"
```

Expected: compilation failure — none of `ListScopeAdminsQuery`, `ListScopeAdminsQueryValidator`, `ListScopeAdminsQueryHandler` exist.

- [ ] **Step 3: Create the query type**

Create `src/Application/ArturRios.Heimdall.Query/Input/ListScopeAdminsQuery.cs`:

```csharp
using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to list the system's <c>ScopeAdmin</c> persons, with pagination and optional filtering
///     (UC-07 read d, FR-PE-12). This is what backs an owner picker: UI-11 selects a scope's first
///     owners before the scope exists, and UI-14 adds an existing Scope Admin as a co-owner.
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never taken from the request; both are <c>[JsonIgnore]</c>,
///     which <c>ServerPopulatedBindingMetadataProvider</c> turns into "not bindable", so they never
///     reach the public contract.
/// </summary>
/// <remarks>
///     There is deliberately no <c>IncludeDeleted</c>. A logically deleted administrator is never a
///     valid owner — <c>PersonNotValidScopeAdmin</c> refuses one — so listing them could only offer
///     a picker entry whose submission is guaranteed to fail.
/// </remarks>
public class ListScopeAdminsQuery : BaseQuery, IActorScoped
{
    /// <summary>Optional case-insensitive substring filter on the administrator's name.</summary>
    public string? Name { get; set; }

    /// <summary>Optional case-insensitive substring filter on the administrator's email.</summary>
    public string? Email { get; set; }

    /// <summary>
    ///     When set, the current owners of this scope are removed from the results (UI-14 AF-14c).
    ///     The exclusion runs before pagination, so a page is not silently short. The caller must be
    ///     entitled to manage the named scope: without that check, running the query with and
    ///     without this parameter and diffing the two results would enumerate the owners of any
    ///     scope, which is exactly what this endpoint's minimal projection exists to prevent.
    /// </summary>
    public Guid? ExcludeOwnersOfScopeId { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
```

- [ ] **Step 4: Create the validator**

Create `src/Application/ArturRios.Heimdall.Query/Input/Validation/ListScopeAdminsQueryValidator.cs`:

```csharp
using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Query.Input.Validation;

/// <summary>Input validation for <see cref="ListScopeAdminsQuery" /> (UC-07 read d, NFR-10).</summary>
public class ListScopeAdminsQueryValidator : PaginatedQueryValidator<ListScopeAdminsQuery>
{
    public ListScopeAdminsQueryValidator()
    {
        // Matches Person.Name/Email's own [MaxLength] — a longer filter could never match a row.
        RuleFor(query => query.Name)
            .MaximumLength(200)
            .WithMessage(PaginationMessages.FilterTooLong);

        RuleFor(query => query.Email)
            .MaximumLength(256)
            .WithMessage(PaginationMessages.FilterTooLong);
    }
}
```

- [ ] **Step 5: Create the output type**

Create `src/Application/ArturRios.Heimdall.Query/Output/PersonSummaryOutput.cs`:

```csharp
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     The minimum a person picker needs: who they are and how to recognise them (FR-PE-12).
/// </summary>
/// <remarks>
///     A separate type from <see cref="PersonOutput" />, and deliberately so. UC-07's visibility rule
///     otherwise lets a Scope Admin see only the administrators co-owning their own scopes, and
///     UI-14 needs them to find one they share no scope with — so this listing's audience is wider
///     than that rule. Three fields is what makes the widening safe: a Scope Admin learns that an
///     administrator with a given name and address exists, which they can already establish by
///     submitting that address to <c>POST /api/scopes/{id}/owners</c> and reading the duplicate-email
///     refusal. Reusing <see cref="PersonOutput" /> would instead hand them <c>Role</c>,
///     <c>OwnedScopeIds</c>, <c>EmailVerified</c>, <c>TwoFactorEnabled</c>, and the timestamps.
/// </remarks>
public class PersonSummaryOutput : QueryOutput
{
    /// <summary>Public identifier of the person.</summary>
    public Guid Id { get; set; }

    /// <summary>Full name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Email address.</summary>
    public string Email { get; set; } = string.Empty;
}
```

- [ ] **Step 6: Create the handler**

Create `src/Application/ArturRios.Heimdall.Query/Handlers/ListScopeAdminsQueryHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="ListScopeAdminsQuery" /> (UC-07 read d, FR-PE-12): lists every live
///     <c>ScopeAdmin</c> with pagination and optional name/email filters, projected to identifier,
///     name, and email only. Backs UI-11's owner selector and UI-14's "add an existing Scope Admin".
/// </summary>
/// <remarks>
///     <para>
///         Both administrator roles may call it, which is wider than UC-07's per-person visibility
///         rule allows for a Scope Admin. That widening is deliberate and is what makes UI-14 step 3
///         possible at all — a co-owner being added does not yet own the scope, so the existing rule
///         could never surface them. The three-field projection is what keeps it safe; see
///         <see cref="PersonSummaryOutput" />.
///     </para>
///     <para>
///         <c>ExcludeOwnersOfScopeId</c> is gated on scope ownership even though the projection is
///         minimal, because the parameter is not a projection question: calling the endpoint twice,
///         once with it and once without, and diffing the results enumerates the owners of whatever
///         scope was named. The gate makes that possible only for a scope the caller already manages.
///     </para>
/// </remarks>
public class ListScopeAdminsQueryHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<Person> personReader,
    IScopeOwnershipChecker scopeOwnership,
    IValidator<ListScopeAdminsQuery> validator)
    : IPaginatedQueryHandlerAsync<ListScopeAdminsQuery, PersonSummaryOutput>
{
    public async Task<PaginatedOutput<PersonSummaryOutput>> HandleAsync(ListScopeAdminsQuery query)
    {
        var output = PaginatedOutput<PersonSummaryOutput>.New;

        // NFR-10: page number/size bounds and filter length, validated before any query runs.
        var validation = await validator.ValidateAsync(query);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        long? excludedScopeId = null;

        if (query.ExcludeOwnersOfScopeId is not null)
        {
            // AF-07a: the named scope must exist and not be logically deleted.
            var scope = await scopeReader.Query()
                .FirstOrDefaultAsync(x => x.PublicId == query.ExcludeOwnersOfScopeId.Value && !x.IsDeleted);

            if (scope is null)
            {
                return output.WithError(PersonMessages.ScopeNotFound);
            }

            // AF-07b: only an owner of the named scope (or a System Admin) may subtract its owners,
            // since a with/without diff would otherwise reveal them.
            if (!await scopeOwnership.ActorMayManageScopeAsync(query.ActingRole, query.ActingPersonId, scope.Id))
            {
                return output.WithError(PersonMessages.NotScopeOwner);
            }

            excludedScopeId = scope.Id;
        }

        // A logically deleted administrator is never a valid owner, so this listing has no
        // include-deleted mode at all — see ListScopeAdminsQuery.
        var admins = personReader.Query()
            .Where(x => x.RoleId == (long)Roles.ScopeAdmin && !x.IsDeleted);

        if (excludedScopeId is not null)
        {
            // Before pagination, so a page of the requested size comes back full (UI-14 AF-14c).
            admins = admins.Where(x => x.ScopeOwnerships.All(ownership => ownership.ScopeId != excludedScopeId.Value));
        }

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name.ToLower();
            admins = admins.Where(x => x.Name.ToLower().Contains(name));
        }

        if (!string.IsNullOrWhiteSpace(query.Email))
        {
            var email = query.Email.ToLower();
            admins = admins.Where(x => x.Email.ToLower().Contains(email));
        }

        var projected = admins.Select(x => new PersonSummaryOutput
        {
            Id = x.PublicId,
            Name = x.Name,
            Email = x.Email
        });

        // Ordered by name with the public identifier as a tiebreaker, then paginated over that
        // ordering — the same reasoning ListScopePersonsQueryHandler documents: names are not
        // unique, PostgreSQL gives no ordering guarantee between tied sort keys, and each page is a
        // separate query, so without the tiebreaker two administrators sharing a name could straddle
        // a page boundary and appear on both pages while a third appeared on neither.
        var ordered = projected.OrderBy(x => x.Name).ThenBy(x => x.Id);

        var page = await ordered.PaginateAsync(query.PageNumber, query.PageSize, orderBy: null);

        return page.WithMessage(PersonMessages.PersonsRetrievedSuccessfully);
    }
}
```

- [ ] **Step 7: Run the unit tests to verify they pass**

```bash
dotnet test tests/Application/ArturRios.Heimdall.Query.Tests --filter "FullyQualifiedName~ListScopeAdminsQueryHandlerTests"
```

Expected: PASS, 12 tests.

- [ ] **Step 8: Register the handler and validator**

In `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`, after the `ListScopeOwnersQueryHandler` registration:

```csharp
        Builder.Services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopeAdminsQuery, PersonSummaryOutput>,
                ListScopeAdminsQueryHandler>();
```

And in the validator block, after `ListScopeOwnersQueryValidator`:

```csharp
        Builder.Services.AddScoped<IValidator<ListScopeAdminsQuery>, ListScopeAdminsQueryValidator>();
```

- [ ] **Step 9: Add the controller action**

Append to `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/PersonController.cs`, after `ListScopeOwners`:

```csharp
    /// <summary>
    ///     Lists the system's <c>ScopeAdmin</c> persons (UC-07 read d, FR-PE-12), projected to
    ///     identifier, name, and email — the source for an owner picker. A System Admin or a Scope
    ///     Admin may call it. Optionally excludes the current owners of a named scope, in which case
    ///     the handler requires the caller to be entitled to manage that scope.
    /// </summary>
    [HttpGet("persons/scope-admins")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<PaginatedOutput<PersonSummaryOutput>>> ListScopeAdmins(
        [FromQuery] ListScopeAdminsQuery query)
    {
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListScopeAdminsQuery, PersonSummaryOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }
```

The route has no `{id:guid}` ambiguity: `persons/{id:guid}` cannot match the literal `scope-admins`.

- [ ] **Step 10: Write the failing functional tests**

Create `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/PersonControllerListScopeAdminsTests.cs`. Copy the `SeedScopeAsync` and `SeedScopeAdminAsync` helpers verbatim from `PersonControllerListScopeOwnersTests.cs` (they seed a scope, and an admin optionally owning one), then:

```csharp
    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenListScopeAdmins_ThenReturnsEveryLiveScopeAdmin()
    {
        // Given two Scope Admins with a shared, distinctive name fragment
        var marker = $"pick{Guid.NewGuid():N}";
        await SeedScopeAdminAsync(name: $"Ana {marker}");
        await SeedScopeAdminAsync(name: $"Bruno {marker}");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/scope-admins?pageNumber=1&pageSize=10&name={marker}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Body?.TotalItems);
    }

    [FunctionalFact]
    public async Task GivenScopeAdmin_WhenListScopeAdmins_ThenTheyMayReadTheListing()
    {
        // Given a Scope Admin who shares no scope with the administrator they are looking for —
        // the case UC-07's own visibility rule could never surface (UI-14 step 3)
        var marker = $"pick{Guid.NewGuid():N}";
        var scope = await SeedScopeAsync();
        var caller = await SeedScopeAdminAsync(ownedScope: scope, name: $"Caller {marker}");
        await SeedScopeAdminAsync(name: $"Stranger {marker}");
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/scope-admins?pageNumber=1&pageSize=10&name={marker}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Body?.TotalItems);
    }

    [FunctionalFact]
    public async Task GivenExcludeOwnersOfOwnScope_WhenListScopeAdmins_ThenCurrentOwnersAreRemoved()
    {
        // Given a scope the caller owns, and one other administrator (UI-14 AF-14c)
        var marker = $"pick{Guid.NewGuid():N}";
        var scope = await SeedScopeAsync();
        var caller = await SeedScopeAdminAsync(ownedScope: scope, name: $"Owner {marker}");
        var candidate = await SeedScopeAdminAsync(name: $"Candidate {marker}");
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/scope-admins?pageNumber=1&pageSize=10&name={marker}" +
            $"&excludeOwnersOfScopeId={scope.PublicId}");

        // Then — the caller, already an owner, is gone; the candidate remains
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
        Assert.Equal(candidate.PublicId, response.Body?.Data?.Single().Id);
    }

    [FunctionalFact]
    public async Task GivenExcludeOwnersOfAnotherScope_WhenListScopeAdmins_ThenReturnsForbidden()
    {
        // Given a Scope Admin naming a scope they do not own. This is the regression test for the
        // enumeration leak: with/without diffing would otherwise reveal that scope's owners.
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var caller = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/scope-admins?pageNumber=1&pageSize=10&excludeOwnersOfScopeId={otherScope.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(PersonMessages.NotScopeOwner, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenUnknownScopeToExclude_WhenListScopeAdmins_ThenReturnsNotFound()
    {
        // Given no such scope
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/scope-admins?pageNumber=1&pageSize=10&excludeOwnersOfScopeId={Guid.NewGuid()}");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPlainUser_WhenListScopeAdmins_ThenReturnsForbidden()
    {
        // Given a User, whom the role gate excludes
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            "/api/persons/scope-admins?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnauthenticatedCaller_WhenListScopeAdmins_ThenReturnsUnauthorized()
    {
        // Given no bearer token
        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            "/api/persons/scope-admins?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
```

Use the same class header, `[Collection(nameof(FunctionalCollection))]` attribute, constructor signature, and `using` block as `PersonControllerListScopeOwnersTests.cs`, adding `ArturRios.Heimdall.Shared.Messages` for the message assertion. Each test filters by its own random `marker` because the listing is system-wide and the fixture seeds stand-in persons — an unfiltered count would be shared state between tests.

`SeedScopeAdminAsync` in the source file has signature `(Scope? ownedScope = null, string name = "Admin")`; the calls above rely on that, so copy it unchanged.

- [ ] **Step 11: Build and run**

```bash
dotnet build src/ArturRios.Heimdall.sln
```

Expected: no errors.

```bash
dotnet test tests/Presentation/ArturRios.Heimdall.WebApi.Tests --filter "FullyQualifiedName~PersonControllerListScopeAdminsTests"
```

Expected: PASS, 7 tests.

- [ ] **Step 12: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Query src/Presentation/ArturRios.Heimdall.WebApi tests/Application/ArturRios.Heimdall.Query.Tests/ListScopeAdminsQueryHandlerTests.cs tests/Presentation/ArturRios.Heimdall.WebApi.Tests/PersonControllerListScopeAdminsTests.cs
git commit -m "feat: list scope admins for the owner pickers"
```

---

### Task 4: Requirements and use case documents

**Files:**
- Modify: `docs/requirements/System Requirements Document.md` (FR-PE table ~line 122-130; FR-2F table ~line 235-248; traceability ~line 973 and ~982)
- Modify: `docs/requirements/Use Case Specification Document.md` (UC-07 ~line 450-490; UC-36–UC-40 ~line 1563-1800)

**Interfaces:**
- Consumes: the endpoint behaviour built in Tasks 1–3. Nothing consumes this task; Task 5 documents the same endpoints for readers rather than for the specification.

- [ ] **Step 1: Add FR-PE-12**

In `docs/requirements/System Requirements Document.md`, after the FR-PE-11 row:

```markdown
| FR-PE-12 | The system shall allow listing the `ScopeAdmin` persons of the system, with pagination and optional case-insensitive name and email filters, and optional exclusion of the current owners of a named scope. Readable by a `SystemAdmin` or a `ScopeAdmin`. The projection is the person's identifier, name, and email only — no role, owned scopes, verification, two-factor state, or timestamps. When the exclusion is requested, the caller must be entitled to manage the named scope, since comparing the filtered and unfiltered results would otherwise enumerate that scope's owners | High |
```

- [ ] **Step 2: Amend FR-PE-04**

Append to the FR-PE-04 description, before the closing `|`:

```
. The person projection reports whether the person has an active two-factor configuration, but never which methods are configured (see FR-2F-15)
```

- [ ] **Step 3: Add FR-2F-15**

After the FR-2F-14 row:

```markdown
| FR-2F-15 | The system shall allow an authenticated person to read their own two-factor authentication state: whether it is active, which methods are configured, and how many issued recovery codes remain unused. A person with no configuration shall be answered successfully with every flag false and a zero count, since never having enabled it is an ordinary state rather than a fault. A Google User shall be refused, as under FR-2F-01. No secret is returned — never the TOTP secret, never a recovery code | High |
```

- [ ] **Step 4: Update the traceability rows**

Change `| Person CRUD | FR-PE-01 through FR-PE-11 |` to `FR-PE-12`, and `| Two-Factor Authentication | FR-2F-01 through FR-2F-14 |` to `FR-2F-15`. Search for the literal strings — the line numbers shift as rows are added above them.

- [ ] **Step 5: Add UC-07 read d**

In `docs/requirements/Use Case Specification Document.md`, in UC-07's header table, replace the **Description** value with:

```markdown
| **Description** | Retrieve a person's details or list persons. There are four distinct reads: (a) a single person by ID, via `GET /api/persons/{id}`; (b) the `User` persons of a scope, via `GET /api/scopes/{scopeId}/persons`; (c) the `ScopeAdmin` owners of a scope, via `GET /api/scopes/{scopeId}/owners`; or (d) the `ScopeAdmin` persons of the system, via `GET /api/persons/scope-admins` |
```

and the **Preconditions** value with:

```markdown
| **Preconditions** | Actor is authenticated; for reads (b) and (c), the target scope exists and is not logically deleted; for read (d), the scope named for exclusion — if any — exists and is not logically deleted |
```

After the "Main Flow (read c — list the owners of a scope)" block, add:

```markdown
**Main Flow (read d — list the system's Scope Admins):**

1. A System Admin or a Scope Admin requests the system's `ScopeAdmin` persons, optionally filtering by name or email, optionally naming a scope whose current owners are to be excluded, and paging the result (FR-PE-12).
2. If a scope was named for exclusion, the system verifies it exists, is not logically deleted, and that the actor may manage it: a System Admin always may; a Scope Admin must own it.
3. The system returns every `ScopeAdmin` person that is not logically deleted, less the named scope's current owners if one was named, projected to the person's identifier, name, and email only.
```

Then extend the alternative-flow rows to name read d:

```markdown
| AF-07a | Person not found, or logically deleted and not explicitly requested (read a); target scope not found or logically deleted (reads b, c); scope named for exclusion not found or logically deleted (read d) | Return `404 Not Found` |
| AF-07b | Actor not authorized to view the requested person (read a); actor is not an owner of the target scope (reads b, c); actor is not an owner of the scope named for exclusion (read d) | Return `403 Forbidden` |
```

- [ ] **Step 6: Record the visibility decision under UC-07**

Add a note under UC-07, in the style of UC-02's "On the list read being System-Admin-only" note:

```markdown
> **On read d being open to a Scope Admin.** Read a's visibility rule lets a Scope Admin see only
> the Scope Admins co-owning the scopes they own. Read d is deliberately wider: UI-14 has a Scope
> Admin add a co-owner who, by definition, does not own the scope yet, so under read a's rule that
> person could never be found and the flow could not be completed at all. What makes the widening
> safe is the projection, not the audience — identifier, name, and email, and nothing else. A Scope
> Admin learns that an administrator with a given address exists, which they can already establish
> by submitting that address to UC-06 path c and reading the duplicate-email refusal. The
> exclusion parameter is gated separately, because it is a different question: subtracting a
> scope's owners from a list reveals them by comparison, so only an actor entitled to manage that
> scope may ask for it.
```

- [ ] **Step 7: Add the status read to the two-factor use cases**

The status read is folded into UC-36, which is where a configuration comes into existence and therefore where its state is described. Rename UC-36 and give it two reads, following UC-07's own multi-read convention.

In UC-36's header table, replace the **Name**, **Description**, and **Postconditions** values with:

```markdown
| **Name** | Enable Two-Factor Authentication (Initiate Setup and Read Status) |
| **Description** | Begin opting an authenticated person into two-factor authentication, selecting an authenticator-app method, an email method, or both; and read a person's own current two-factor state. Setup is inactive until confirmed by UC-37. There are two operations: (a) initiate setup, via `POST /api/auth/2fa/enable`; and (b) read the caller's own status, via `GET /api/auth/2fa` |
| **Postconditions** | For (a): a `TWO_FACTOR_AUTH` row exists for the person with `IsActive = false`; for the App method, a TOTP secret has been generated and returned; for the Email method, a first code has been emailed. For (b): nothing is changed |
```

Retitle the existing "**Main Flow:**" heading to "**Main Flow (a — initiate setup):**", then add after its numbered steps:

```markdown
**Main Flow (b — read the caller's own status):**

1. An authenticated person requests their own two-factor state through `GET /api/auth/2fa` (FR-2F-15). The person read is always the caller, taken from their token — a configuration is never addressed by an identifier in a path.
2. The system loads the caller's `TWO_FACTOR_AUTH` row, if one exists.
3. The system returns whether two-factor authentication is active, which methods are configured, and how many issued recovery codes remain unused. No secret is returned: never the TOTP secret, never a recovery code.
4. A caller with no `TWO_FACTOR_AUTH` row is answered successfully, with every flag false and a zero count. Never having enabled two-factor authentication is an ordinary state, not a fault, and a setup initiated but not yet confirmed is reported as inactive with its selected methods set.
```

Then extend AF-36b's condition to cover the read, since a Google User is refused identically whichever operation they call:

```markdown
| AF-36b | Caller is not a person eligible for two-factor authentication — a Google User, or a token naming no live person (either operation) | Return `403 Forbidden` |
```

Preserve AF-36b's existing outcome text if it differs from the above; only the condition column gains "(either operation)".

- [ ] **Step 8: Verify the documents are internally consistent**

Re-read each edited section. Check that no FR number is duplicated, that every new FR appears in the traceability table exactly once, and that the AF rows you extended still read as single grammatical sentences.

- [ ] **Step 9: Commit**

```bash
git add docs/requirements
git commit -m "docs: specify FR-PE-12 and FR-2F-15"
```

---

### Task 5: API reference, HTTP/Bruno clients, and OpenAPI

**Files:**
- Modify: `docs/content/en/docs/api-reference.md` (person endpoint table ~line 72-81; auth section)
- Modify: `api-client/http/auth.http`
- Modify: `api-client/http/persons.http`
- Create: `api-client/bruno/Auth/Get two factor status.bru`
- Create: `api-client/bruno/Persons/List scope admins.bru`
- Modify: `docs/openapi/heimdall.json` (regenerated, not hand-edited)

**Interfaces:**
- Consumes: the routes and query parameters built in Tasks 1 and 3.
- Produces: nothing consumed by later tasks. This is the last task.

- [ ] **Step 1: Add both endpoints to the API reference**

In `docs/content/en/docs/api-reference.md`, add to the person endpoint table, next to the other `GET` rows:

```markdown
| `GET /persons/scope-admins` | System Admin + Scope Admin | UC-07 read d — list Scope Admins for an owner picker; optionally excludes a scope's current owners |
```

And to the auth section, alongside the `2fa` rows:

```markdown
| `GET /auth/2fa` | Any authenticated person | FR-2F-15 — the caller's own two-factor status; 403 for a Google User |
```

Match the table's existing column order and the phrasing style of its neighbours — read the surrounding rows first.

- [ ] **Step 2: Add the HTTP client requests**

Append to `api-client/http/auth.http`, following the file's existing request format (read the 2FA requests already there and copy their variable usage and separator style):

```
### Get two-factor status (FR-2F-15)
GET {{host}}/api/auth/2fa
Authorization: Bearer {{token}}
```

Append to `api-client/http/persons.http`:

```
### List scope admins for an owner picker (UC-07 read d, FR-PE-12)
GET {{host}}/api/persons/scope-admins?pageNumber=1&pageSize=10
Authorization: Bearer {{token}}

### List scope admins excluding a scope's current owners (UI-14 AF-14c)
GET {{host}}/api/persons/scope-admins?pageNumber=1&pageSize=10&excludeOwnersOfScopeId={{scopeId}}
Authorization: Bearer {{token}}
```

If `{{scopeId}}` is not an existing variable in `api-client/http/http-client.env.json`, use whatever the file's other scope-addressed requests use.

- [ ] **Step 3: Add the Bruno requests**

Create `api-client/bruno/Auth/Get two factor status.bru`:

```
meta {
  name: Get two factor status
  type: http
  seq: SEQ
}

get {
  url: {{host}}/api/auth/2fa
  body: none
  auth: inherit
}

docs {
  FR-2F-15. Any authenticated person, for themselves only.

  Returns whether two-factor authentication is active, which methods are configured, and how many recovery codes remain unused. A caller who never enabled it gets a 200 with every flag false; a Google User gets a 403.
}
```

Create `api-client/bruno/Persons/List scope admins.bru`:

```
meta {
  name: List scope admins
  type: http
  seq: SEQ
}

get {
  url: {{host}}/api/persons/scope-admins?pageNumber=1&pageSize=10
  body: none
  auth: inherit
}

docs {
  UC-07 read d, FR-PE-12. System Admin or Scope Admin.

  Backs the owner pickers: UI-11 selects a new scope's first owners, UI-14 adds an existing Scope Admin as a co-owner. Returns identifier, name, and email only.

  Add &excludeOwnersOfScopeId={{scopeId}} to drop the scope's current owners from the results. That parameter requires the caller to own the named scope.
}
```

Replace each `SEQ` with the next unused number in that folder — `ls api-client/bruno/Auth` and `ls api-client/bruno/Persons`, then read the highest `seq:` already present and add one.

- [ ] **Step 4: Regenerate the OpenAPI document**

```bash
python scripts/openapi.py
```

Expected: rewrites `docs/openapi/heimdall.json`. Do not hand-edit that file.

- [ ] **Step 5: Verify the document is current**

```bash
python scripts/openapi.py --check
```

Expected: exit 0. A non-zero exit means the regeneration did not take — rerun step 4 without `--no-build`.

- [ ] **Step 6: Run the contract tests**

```bash
dotnet test tests/Presentation/ArturRios.Heimdall.WebApi.Tests --filter "FullyQualifiedName~OpenApiContractTests"
```

Expected: PASS. These fail before step 4 and pass after it.

- [ ] **Step 7: Run the whole suite**

```bash
dotnet test src/ArturRios.Heimdall.sln
```

Expected: PASS, everything. Do not proceed past a failure — read the `.trx` under the failing project's `TestResults/` for the test's name.

- [ ] **Step 8: Commit**

```bash
git add docs api-client
git commit -m "docs: document the 2FA status and admin listing"
```

---

## Verification

After all five tasks:

```bash
dotnet build src/ArturRios.Heimdall.sln
```

```bash
dotnet test src/ArturRios.Heimdall.sln
```

```bash
python scripts/openapi.py --check
```

All three must succeed before the branch is offered for review.
