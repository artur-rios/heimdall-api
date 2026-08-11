# NFR-09 Audit Logging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every successful write operation (all 36 registered `ICommandHandlerAsync<TCommand, TOutput>` command handlers) produces an `AuditLog` row, satisfying NFR-09, without changing any existing handler's internals.

**Architecture:** A generic decorator (`AuditingCommandHandler<TCommand, TOutput>`) wraps every command handler at DI-registration time. It calls the inner handler, and on success writes one `AuditLog` row (actor, action, target id, timestamp) through a small `IAuditLogWriter`. The actor is read via a new `IActorAccessor` abstraction (interface in the Application layer, `HttpContext`-backed implementation in the Presentation layer) to respect the existing layering — `IdentityUser` is a Presentation-layer type.

**Tech Stack:** .NET 10, EF Core 10 (Npgsql/PostgreSQL, snake_case naming convention), `ArturRios.Mediator` (custom CQRS library, no pipeline-behavior support), xUnit + Moq for unit tests, Testcontainers PostgreSQL for functional tests.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-08-11-nfr-09-audit-logging-design.md` — read it before starting.
- No handler in `src/Application/ArturRios.Heimdall.Command/Handlers/**` is modified. All wiring happens in DI registration (`Startup.cs`) and the new decorator.
- Only successful writes (`result.Success == true`) produce an audit entry.
- No raw command payload is ever persisted — only actor id/role, the command's CLR type name, target id, and timestamp.
- An `IAuditLogWriter` failure must never fail the wrapped write — catch and log via `ILogger<T>`, never rethrow.
- Follow `docs/requirements/Development Workflow Document.md`: one branch, one issue, one pull request. Branch name: `feature/nfr-09-audit-logging-for-write-operations`.
- All new/changed code targets `net10.0`, `ImplicitUsings` and `Nullable` enabled, matching every existing `.csproj` in this repo.
- Test naming convention: `GivenX_WhenY_ThenZ`, `[UnitFact]` for unit tests (`ArturRios.Util.Test.Attributes`), `[FunctionalFact]` for functional tests.

---

### Task 1: Open the tracking issue and create the branch

**Files:** none (git/GitHub only).

- [ ] **Step 1: Create the GitHub issue**

```bash
gh issue create \
  --title "Audit logging for write operations (NFR-09)" \
  --body "Every successful write (all registered command handlers) should produce an audit log entry. See docs/superpowers/specs/2026-08-11-nfr-09-audit-logging-design.md for the design." \
  --label "platform"
```

Note the returned issue number — it is needed for the PR (`Closes #<number>`) and the README update in Task 8.

- [ ] **Step 2: Create the branch from an up-to-date `main`**

```bash
git switch main
git pull
git switch -c feature/nfr-09-audit-logging-for-write-operations
```

- [ ] **Step 3: Move the issue to In Progress**

On the GitHub project board, set the issue's `Status` to **In Progress** (manual step if no board automation is configured; skip if the repo has none).

---

### Task 2: `AuditLog` entity, EF Core mapping, and migration

**Files:**
- Create: `src/Domain/ArturRios.Heimdall.Domain/Entities/AuditLog.cs`
- Create: `src/Infrastructure/ArturRios.Heimdall.Data/EntityMaps/AuditLogDbMap.cs`
- Modify: `src/Infrastructure/ArturRios.Heimdall.Data/Configuration/AppDbContext.cs`
- Create (generated): `src/Infrastructure/ArturRios.Heimdall.Data/Migrations/<timestamp>_AddAuditLog.cs` (+ `.Designer.cs`), and an update to `AppDbContextModelSnapshot.cs`

**Interfaces:**
- Produces: `AuditLog` class with `PublicId: Guid`, `ActorPersonId: Guid?`, `ActorRole: int?`, `Action: string`, `TargetId: Guid?`, `CreatedAt: DateTime`. `ActorPersonId` is a bare `Guid` column — no FK to `Person` — so an audit row survives a hard-deleted person.

- [ ] **Step 1: Create the entity**

```csharp
// src/Domain/ArturRios.Heimdall.Domain/Entities/AuditLog.cs
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     One audit trail entry for a successful write operation (NFR-09). Append-only: never updated
///     or logically deleted after creation. <see cref="ActorPersonId" /> is a bare <c>PublicId</c>,
///     not a foreign key, so an entry survives a hard-deleted person.
/// </summary>
public class AuditLog : Entity
{
    /// <summary>External identifier of this entry.</summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>The acting person's <c>PublicId</c>; <c>null</c> for an anonymous write.</summary>
    public Guid? ActorPersonId { get; set; }

    /// <summary>The acting person's role value (see <c>Roles</c>); <c>null</c> for an anonymous write.</summary>
    public int? ActorRole { get; set; }

    /// <summary>The command's CLR type name, e.g. <c>"CreateApplicationCommand"</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Best-effort public identifier of the entity the write affected; <c>null</c> if none could be resolved.</summary>
    public Guid? TargetId { get; set; }

    /// <summary>When the entry was written.</summary>
    public DateTime CreatedAt { get; set; }
}
```

- [ ] **Step 2: Create the entity map**

```csharp
// src/Infrastructure/ArturRios.Heimdall.Data/EntityMaps/AuditLogDbMap.cs
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArturRios.Heimdall.Data.EntityMaps;

internal static class AuditLogDbMap
{
    public static void Configure(this EntityTypeBuilder<AuditLog> auditLog)
    {
        auditLog.ToTable("audit_log");
        auditLog.HasKey(x => x.Id);

        auditLog.Property(x => x.PublicId).IsRequired();
        auditLog.HasIndex(x => x.PublicId).IsUnique();

        auditLog.Property(x => x.Action).IsRequired();
        auditLog.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        // ActorPersonId/ActorRole/TargetId are plain nullable columns — no FK, no required navigation.
    }
}
```

- [ ] **Step 3: Wire the entity into `AppDbContext`**

In `src/Infrastructure/ArturRios.Heimdall.Data/Configuration/AppDbContext.cs`, add the `DbSet` after `TwoFactorRecoveryCodes`:

```csharp
    public DbSet<TwoFactorRecoveryCode> TwoFactorRecoveryCodes { get; init; }
    public DbSet<AuditLog> AuditLogs { get; init; }
```

And add the model-creating call after `TwoFactorRecoveryCode`:

```csharp
        modelBuilder.Entity<TwoFactorRecoveryCode>().Configure();
        modelBuilder.Entity<AuditLog>().Configure();
```

- [ ] **Step 4: Verify the project builds**

Run: `dotnet build src/ArturRios.Heimdall.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 5: Generate the migration**

```bash
python scripts/migrations.py
```

Pick the local environment file, then choose **Create a migration**, name it `AddAuditLog`. This writes
`src/Infrastructure/ArturRios.Heimdall.Data/Migrations/<timestamp>_AddAuditLog.cs` and updates
`AppDbContextModelSnapshot.cs`.

- [ ] **Step 6: Verify the migration**

Run: `dotnet ef migrations list --project src/Infrastructure/ArturRios.Heimdall.Data --startup-project src/Infrastructure/ArturRios.Heimdall.Data`
Expected: `AddAuditLog` appears as the newest migration.

- [ ] **Step 7: Commit**

```bash
git add src/Domain/ArturRios.Heimdall.Domain/Entities/AuditLog.cs \
        src/Infrastructure/ArturRios.Heimdall.Data/EntityMaps/AuditLogDbMap.cs \
        src/Infrastructure/ArturRios.Heimdall.Data/Configuration/AppDbContext.cs \
        src/Infrastructure/ArturRios.Heimdall.Data/Migrations/
git commit -m "feat: add AuditLog entity, mapping, and migration (NFR-09)"
```

---

### Task 3: `IActorAccessor` and its `HttpContext`-backed implementation

**Files:**
- Create: `src/Application/ArturRios.Heimdall.Shared/Security/IActorAccessor.cs`
- Create: `src/Presentation/ArturRios.Heimdall.WebApi/Security/HttpContextActorAccessor.cs`

**Interfaces:**
- Produces: `IActorAccessor { Guid? ActorPersonId { get; } int? ActorRole { get; } }`, and its implementation `HttpContextActorAccessor(IHttpContextAccessor)`.
- Consumes: `HttpContext.GetUser<IdentityUser>()` (from `ArturRios.Util.WebApi.Security.Extensions`, already used by `ActorExtensions.ApplyActor`), `IdentityUser.Id`/`IdentityUser.RoleId` (`src/Presentation/ArturRios.Heimdall.WebApi/Security/IdentityUser.cs`).

- [ ] **Step 1: Create the interface**

```csharp
// src/Application/ArturRios.Heimdall.Shared/Security/IActorAccessor.cs
namespace ArturRios.Heimdall.Shared.Security;

/// <summary>
///     Reads the authenticated caller for audit logging (NFR-09) without the Application layer
///     depending on Presentation-layer types. Unlike <see cref="IActorScoped" />, this is resolved by
///     the infrastructure (from the request), not populated by the controller onto a command.
/// </summary>
public interface IActorAccessor
{
    /// <summary>The acting caller's person <c>PublicId</c>; <c>null</c> on an anonymous request.</summary>
    Guid? ActorPersonId { get; }

    /// <summary>The acting caller's role value (see <c>Roles</c>); <c>null</c> on an anonymous request.</summary>
    int? ActorRole { get; }
}
```

- [ ] **Step 2: Create the implementation**

```csharp
// src/Presentation/ArturRios.Heimdall.WebApi/Security/HttpContextActorAccessor.cs
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Util.WebApi.Security.Extensions;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     <see cref="IActorAccessor" /> backed by the current request's <see cref="IdentityUser" />, the
///     same source <see cref="ActorExtensions.ApplyActor" /> reads. <c>null</c> on an anonymous
///     request (no authenticated user attached by <c>AuthenticationMiddleware</c>).
/// </summary>
public class HttpContextActorAccessor(IHttpContextAccessor httpContextAccessor) : IActorAccessor
{
    public Guid? ActorPersonId => httpContextAccessor.HttpContext?.GetUser<IdentityUser>()?.Id;

    public int? ActorRole => httpContextAccessor.HttpContext?.GetUser<IdentityUser>()?.RoleId;
}
```

- [ ] **Step 3: Verify the project builds**

Run: `dotnet build src/ArturRios.Heimdall.sln`
Expected: Build succeeds with no errors. (No unit test here — this class is a thin binding to the ASP.NET Core request pipeline; it is exercised end-to-end by the functional test in Task 7, the same way `ActorExtensions` has no isolated unit test today.)

- [ ] **Step 4: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Shared/Security/IActorAccessor.cs \
        src/Presentation/ArturRios.Heimdall.WebApi/Security/HttpContextActorAccessor.cs
git commit -m "feat: add IActorAccessor for audit logging actor resolution"
```

---

### Task 4: `IAuditLogWriter`

**Files:**
- Create: `src/Application/ArturRios.Heimdall.Command/Auditing/IAuditLogWriter.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Auditing/AuditLogWriter.cs`
- Test: `tests/Application/ArturRios.Heimdall.Command.Tests/Auditing/AuditLogWriterTests.cs`

**Interfaces:**
- Consumes: `IActorAccessor` (Task 3), `IAsyncRepository<AuditLog>` (`ArturRios.Data.Relational.Core`, resolved generically — no new DI registration needed, the same way `IAsyncRepository<Application>` resolves in `CreateApplicationCommandHandler`).
- Produces: `IAuditLogWriter { Task WriteAsync(string action, Guid? targetId); }`, consumed by `AuditingCommandHandler<TCommand, TOutput>` in Task 5.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Application/ArturRios.Heimdall.Command.Tests/Auditing/AuditLogWriterTests.cs
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests.Auditing;

public class AuditLogWriterTests
{
    private static IActorAccessor Actor(Guid? personId, int? role)
    {
        var actor = new Mock<IActorAccessor>();
        actor.SetupGet(a => a.ActorPersonId).Returns(personId);
        actor.SetupGet(a => a.ActorRole).Returns(role);
        return actor.Object;
    }

    [UnitFact]
    public async Task GivenAuthenticatedActor_WhenWritingEntry_ThenRowCarriesActorActionAndTarget()
    {
        // Given
        var repository = new AsyncFakeRepository<AuditLog>();
        var personId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var writer = new AuditLogWriter(repository, Actor(personId, 2));

        // When
        await writer.WriteAsync("CreateApplicationCommand", targetId);

        // Then
        var stored = (await repository.GetAllAsync()).Data!.Single();
        Assert.Equal(personId, stored.ActorPersonId);
        Assert.Equal(2, stored.ActorRole);
        Assert.Equal("CreateApplicationCommand", stored.Action);
        Assert.Equal(targetId, stored.TargetId);
        Assert.NotEqual(Guid.Empty, stored.PublicId);
    }

    [UnitFact]
    public async Task GivenAnonymousActor_WhenWritingEntry_ThenActorFieldsAreNull()
    {
        // Given
        var repository = new AsyncFakeRepository<AuditLog>();
        var writer = new AuditLogWriter(repository, Actor(null, null));

        // When
        await writer.WriteAsync("PasswordRecoveryCommand", null);

        // Then
        var stored = (await repository.GetAllAsync()).Data!.Single();
        Assert.Null(stored.ActorPersonId);
        Assert.Null(stored.ActorRole);
        Assert.Null(stored.TargetId);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Application/ArturRios.Heimdall.Command.Tests --filter "FullyQualifiedName~AuditLogWriterTests"`
Expected: FAIL — `AuditLogWriter`/`IAuditLogWriter` do not exist yet (compile error).

- [ ] **Step 3: Write the interface and implementation**

```csharp
// src/Application/ArturRios.Heimdall.Command/Auditing/IAuditLogWriter.cs
namespace ArturRios.Heimdall.Command.Auditing;

/// <summary>Persists one audit trail entry (NFR-09). See <see cref="AuditingCommandHandler{TCommand,TOutput}" />.</summary>
public interface IAuditLogWriter
{
    /// <param name="action">The command's CLR type name, e.g. <c>"CreateApplicationCommand"</c>.</param>
    /// <param name="targetId">Best-effort public identifier of the affected entity, if resolvable.</param>
    Task WriteAsync(string action, Guid? targetId);
}
```

```csharp
// src/Application/ArturRios.Heimdall.Command/Auditing/AuditLogWriter.cs
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Security;

namespace ArturRios.Heimdall.Command.Auditing;

public class AuditLogWriter(IAsyncRepository<AuditLog> repository, IActorAccessor actorAccessor)
    : IAuditLogWriter
{
    public async Task WriteAsync(string action, Guid? targetId)
    {
        var entry = new AuditLog
        {
            PublicId = Guid.NewGuid(),
            ActorPersonId = actorAccessor.ActorPersonId,
            ActorRole = actorAccessor.ActorRole,
            Action = action,
            TargetId = targetId,
            CreatedAt = DateTime.UtcNow
        };

        await repository.CreateAsync(entry);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Application/ArturRios.Heimdall.Command.Tests --filter "FullyQualifiedName~AuditLogWriterTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Command/Auditing/IAuditLogWriter.cs \
        src/Application/ArturRios.Heimdall.Command/Auditing/AuditLogWriter.cs \
        tests/Application/ArturRios.Heimdall.Command.Tests/Auditing/AuditLogWriterTests.cs
git commit -m "feat: add IAuditLogWriter"
```

---

### Task 5: `AuditingCommandHandler` decorator and DI registration extension

**Files:**
- Create: `src/Application/ArturRios.Heimdall.Command/Auditing/AuditingCommandHandler.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Auditing/CommandHandlerRegistrationExtensions.cs`
- Modify: `src/Application/ArturRios.Heimdall.Command/ArturRios.Heimdall.Command.csproj`
- Test: `tests/Application/ArturRios.Heimdall.Command.Tests/Auditing/AuditingCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `IAuditLogWriter` (Task 4), `ICommandHandlerAsync<TCommand, TOutput>` (`ArturRios.Mediator.Command.Interfaces`), `DataOutput<TOutput?>` (`ArturRios.Output`).
- Produces: `AuditingCommandHandler<TCommand, TOutput>(ICommandHandlerAsync<TCommand, TOutput> inner, IAuditLogWriter auditLogWriter, ILogger<AuditingCommandHandler<TCommand, TOutput>> logger)`, and the extension method `IServiceCollection.AddAuditedCommandHandler<TCommand, TOutput, THandler>()`, both consumed by `Startup.cs` in Task 6.

- [ ] **Step 1: Add the two new package references**

In `src/Application/ArturRios.Heimdall.Command/ArturRios.Heimdall.Command.csproj`, add inside the existing `<ItemGroup>` of `PackageReference`s:

```xml
      <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.10" />
      <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.10" />
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/Application/ArturRios.Heimdall.Command.Tests/Auditing/AuditingCommandHandlerTests.cs
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ArturRios.Heimdall.Command.Tests.Auditing;

public record StubCommand;

public class StubOutput
{
    public Guid Id { get; set; }
}

public class AuditingCommandHandlerTests
{
    private static ILogger<AuditingCommandHandler<StubCommand, StubOutput>> NullLogger() =>
        NullLogger<AuditingCommandHandler<StubCommand, StubOutput>>.Instance;

    [UnitFact]
    public async Task GivenSuccessfulInnerHandler_WhenHandling_ThenWriterIsCalledWithActionAndTargetId()
    {
        // Given an inner handler that succeeds and returns an id-bearing output
        var targetId = Guid.NewGuid();
        var inner = new Mock<ICommandHandlerAsync<StubCommand, StubOutput>>();
        inner.Setup(h => h.HandleAsync(It.IsAny<StubCommand>()))
            .ReturnsAsync(DataOutput<StubOutput?>.New.WithData(new StubOutput { Id = targetId }));
        var writer = new Mock<IAuditLogWriter>();
        var handler = new AuditingCommandHandler<StubCommand, StubOutput>(inner.Object, writer.Object, NullLogger());

        // When
        var result = await handler.HandleAsync(new StubCommand());

        // Then
        Assert.True(result.Success);
        writer.Verify(w => w.WriteAsync(nameof(StubCommand), targetId), Times.Once);
    }

    [UnitFact]
    public async Task GivenFailedInnerHandler_WhenHandling_ThenWriterIsNeverCalled()
    {
        // Given an inner handler that fails validation
        var inner = new Mock<ICommandHandlerAsync<StubCommand, StubOutput>>();
        inner.Setup(h => h.HandleAsync(It.IsAny<StubCommand>()))
            .ReturnsAsync(DataOutput<StubOutput?>.New.WithError("invalid"));
        var writer = new Mock<IAuditLogWriter>();
        var handler = new AuditingCommandHandler<StubCommand, StubOutput>(inner.Object, writer.Object, NullLogger());

        // When
        var result = await handler.HandleAsync(new StubCommand());

        // Then
        Assert.False(result.Success);
        writer.Verify(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<Guid?>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenWriterThrows_WhenHandling_ThenOriginalSuccessfulResultIsStillReturned()
    {
        // Given a writer that throws — an audit-logging outage must not fail the underlying write
        var inner = new Mock<ICommandHandlerAsync<StubCommand, StubOutput>>();
        inner.Setup(h => h.HandleAsync(It.IsAny<StubCommand>()))
            .ReturnsAsync(DataOutput<StubOutput?>.New.WithData(new StubOutput { Id = Guid.NewGuid() }));
        var writer = new Mock<IAuditLogWriter>();
        writer.Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<Guid?>()))
            .ThrowsAsync(new InvalidOperationException("db unavailable"));
        var handler = new AuditingCommandHandler<StubCommand, StubOutput>(inner.Object, writer.Object, NullLogger());

        // When
        var result = await handler.HandleAsync(new StubCommand());

        // Then
        Assert.True(result.Success);
    }

    [UnitFact]
    public async Task GivenNullOutputData_WhenHandling_ThenWriterIsCalledWithNullTargetId()
    {
        // Given a successful result carrying a null Data (e.g. a command whose output is empty)
        var inner = new Mock<ICommandHandlerAsync<StubCommand, StubOutput>>();
        inner.Setup(h => h.HandleAsync(It.IsAny<StubCommand>()))
            .ReturnsAsync(DataOutput<StubOutput?>.New.WithData((StubOutput?)null));
        var writer = new Mock<IAuditLogWriter>();
        var handler = new AuditingCommandHandler<StubCommand, StubOutput>(inner.Object, writer.Object, NullLogger());

        // When
        await handler.HandleAsync(new StubCommand());

        // Then
        writer.Verify(w => w.WriteAsync(nameof(StubCommand), null), Times.Once);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Application/ArturRios.Heimdall.Command.Tests --filter "FullyQualifiedName~AuditingCommandHandlerTests"`
Expected: FAIL — `AuditingCommandHandler` does not exist yet (compile error).

- [ ] **Step 4: Write the decorator**

```csharp
// src/Application/ArturRios.Heimdall.Command/Auditing/AuditingCommandHandler.cs
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Auditing;

/// <summary>
///     Wraps a command handler so every successful write produces one audit trail entry (NFR-09),
///     without changing the wrapped handler. Registered per-handler by
///     <see cref="CommandHandlerRegistrationExtensions.AddAuditedCommandHandler{TCommand,TOutput,THandler}" />.
/// </summary>
public class AuditingCommandHandler<TCommand, TOutput>(
    ICommandHandlerAsync<TCommand, TOutput> inner,
    IAuditLogWriter auditLogWriter,
    ILogger<AuditingCommandHandler<TCommand, TOutput>> logger)
    : ICommandHandlerAsync<TCommand, TOutput>
{
    public async Task<DataOutput<TOutput?>> HandleAsync(TCommand command)
    {
        var result = await inner.HandleAsync(command);

        if (result.Success)
        {
            try
            {
                await auditLogWriter.WriteAsync(typeof(TCommand).Name, ResolveTargetId(result.Data));
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Failed to write audit log entry for {Action}", typeof(TCommand).Name);
            }
        }

        return result;
    }

    private static Guid? ResolveTargetId(TOutput? output)
    {
        if (output is null)
        {
            return null;
        }

        var property = typeof(TOutput).GetProperty("Id") ?? typeof(TOutput).GetProperty("PublicId");

        return property is not null && property.PropertyType == typeof(Guid)
            ? (Guid?)property.GetValue(output)
            : null;
    }
}
```

- [ ] **Step 5: Write the DI registration extension**

```csharp
// src/Application/ArturRios.Heimdall.Command/Auditing/CommandHandlerRegistrationExtensions.cs
using ArturRios.Mediator.Command.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ArturRios.Heimdall.Command.Auditing;

public static class CommandHandlerRegistrationExtensions
{
    /// <summary>
    ///     Registers <typeparamref name="THandler" /> and wraps it with <see cref="AuditingCommandHandler{TCommand,TOutput}" />
    ///     so every command handler produces an audit trail entry on success (NFR-09), without any
    ///     change to the handler itself.
    /// </summary>
    public static IServiceCollection AddAuditedCommandHandler<TCommand, TOutput, THandler>(
        this IServiceCollection services)
        where THandler : class, ICommandHandlerAsync<TCommand, TOutput>
    {
        services.AddScoped<THandler>();
        services.AddScoped<ICommandHandlerAsync<TCommand, TOutput>>(provider =>
            new AuditingCommandHandler<TCommand, TOutput>(
                provider.GetRequiredService<THandler>(),
                provider.GetRequiredService<IAuditLogWriter>(),
                provider.GetRequiredService<
                    Microsoft.Extensions.Logging.ILogger<AuditingCommandHandler<TCommand, TOutput>>>()));

        return services;
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Application/ArturRios.Heimdall.Command.Tests --filter "FullyQualifiedName~AuditingCommandHandlerTests"`
Expected: PASS (4 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Command/ArturRios.Heimdall.Command.csproj \
        src/Application/ArturRios.Heimdall.Command/Auditing/AuditingCommandHandler.cs \
        src/Application/ArturRios.Heimdall.Command/Auditing/CommandHandlerRegistrationExtensions.cs \
        tests/Application/ArturRios.Heimdall.Command.Tests/Auditing/AuditingCommandHandlerTests.cs
git commit -m "feat: add AuditingCommandHandler decorator and DI registration extension"
```

---

### Task 6: Wire the decorator into `Startup.cs`

**Files:**
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`

**Interfaces:**
- Consumes: `IActorAccessor`/`HttpContextActorAccessor` (Task 3), `IAuditLogWriter`/`AuditLogWriter` (Task 4), `AddAuditedCommandHandler<TCommand, TOutput, THandler>` (Task 5).

- [ ] **Step 1: Add the `using` for the auditing namespace**

In `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`, add to the `using` block (alphabetical, next to the other `ArturRios.Heimdall.Command.*` usings):

```csharp
using ArturRios.Heimdall.Command.Auditing;
```

- [ ] **Step 2: Register `IHttpContextAccessor`, `IActorAccessor`, and `IAuditLogWriter`**

In `AddDependencies()`, immediately after the `Builder.Services.AddScoped<CommandMediator>();` line, add:

```csharp
        Builder.Services.AddHttpContextAccessor();
        Builder.Services.AddScoped<IActorAccessor, HttpContextActorAccessor>();
        Builder.Services.AddScoped<IAuditLogWriter, AuditLogWriter>();
```

- [ ] **Step 3: Replace every command handler registration with the audited variant**

The rule: every call of the shape `Builder.Services.AddScoped<ICommandHandlerAsync<TCommand, TOutput>, THandler>();` — whether written on one line or split across two with `Builder.Services` on its own line — becomes a single line `Builder.Services.AddAuditedCommandHandler<TCommand, TOutput, THandler>();`. Every other line (validator registrations, comments, query handler registrations `IQueryHandlerAsync`/`IPaginatedQueryHandlerAsync`, health checks, everything after the `QueryMediator` registration) is untouched — queries are reads, not writes, and are out of scope for NFR-09.

Two worked examples from the existing file:

Before:
```csharp
        Builder.Services
            .AddScoped<ICommandHandlerAsync<CreateScopeCommand, CreateScopeCommandOutput>, CreateScopeCommandHandler>();
```
After:
```csharp
        Builder.Services.AddAuditedCommandHandler<CreateScopeCommand, CreateScopeCommandOutput, CreateScopeCommandHandler>();
```

Before:
```csharp
        Builder.Services
            .AddScoped<ICommandHandlerAsync<SetGoogleSignInCommand, SetGoogleSignInCommandOutput>,
                SetGoogleSignInCommandHandler>();
```
After:
```csharp
        Builder.Services.AddAuditedCommandHandler<SetGoogleSignInCommand, SetGoogleSignInCommandOutput, SetGoogleSignInCommandHandler>();
```

Apply the same rewrite to every row in this table (`TCommand`, `TOutput`, `THandler`) — these are every command handler registration currently in `Startup.cs`, in file order:

| TCommand | TOutput | THandler |
| --- | --- | --- |
| `CreateScopeCommand` | `CreateScopeCommandOutput` | `CreateScopeCommandHandler` |
| `UpdateScopeCommand` | `UpdateScopeCommandOutput` | `UpdateScopeCommandHandler` |
| `DeleteScopeCommand` | `DeleteScopeCommandOutput` | `DeleteScopeCommandHandler` |
| `HardDeleteScopeCommand` | `HardDeleteScopeCommandOutput` | `HardDeleteScopeCommandHandler` |
| `SetGoogleSignInCommand` | `SetGoogleSignInCommandOutput` | `SetGoogleSignInCommandHandler` |
| `CreateAdminCommand` | `CreatePersonCommandOutput` | `CreateAdminCommandHandler` |
| `CreateUserCommand` | `CreatePersonCommandOutput` | `CreateUserCommandHandler` |
| `CreateScopeOwnerCommand` | `CreatePersonCommandOutput` | `CreateScopeOwnerCommandHandler` |
| `AddScopeOwnerCommand` | `AddScopeOwnerCommandOutput` | `AddScopeOwnerCommandHandler` |
| `RemoveScopeOwnerCommand` | `RemoveScopeOwnerCommandOutput` | `RemoveScopeOwnerCommandHandler` |
| `PromoteScopeUserCommand` | `PromoteScopeUserCommandOutput` | `PromoteScopeUserCommandHandler` |
| `UpdatePersonCommand` | `UpdatePersonCommandOutput` | `UpdatePersonCommandHandler` |
| `DeletePersonCommand` | `DeletePersonCommandOutput` | `DeletePersonCommandHandler` |
| `HardDeletePersonCommand` | `HardDeletePersonCommandOutput` | `HardDeletePersonCommandHandler` |
| `LoginCommand` | `LoginCommandOutput` | `LoginCommandHandler` |
| `PasswordRecoveryCommand` | `PasswordRecoveryCommandOutput` | `PasswordRecoveryCommandHandler` |
| `ResetPasswordCommand` | `ResetPasswordCommandOutput` | `ResetPasswordCommandHandler` |
| `VerifyEmailCommand` | `VerifyEmailCommandOutput` | `VerifyEmailCommandHandler` |
| `ResendVerificationEmailCommand` | `ResendVerificationEmailCommandOutput` | `ResendVerificationEmailCommandHandler` |
| `GoogleSignInCommand` | `GoogleSignInCommandOutput` | `GoogleSignInCommandHandler` |
| `GoogleSignOutCommand` | `GoogleSignOutCommandOutput` | `GoogleSignOutCommandHandler` |
| `DeleteGoogleUserCommand` | `DeleteGoogleUserCommandOutput` | `DeleteGoogleUserCommandHandler` |
| `HardDeleteGoogleUserCommand` | `HardDeleteGoogleUserCommandOutput` | `HardDeleteGoogleUserCommandHandler` |
| `CreateApplicationCommand` | `CreateApplicationCommandOutput` | `CreateApplicationCommandHandler` |
| `UpdateApplicationCommand` | `UpdateApplicationCommandOutput` | `UpdateApplicationCommandHandler` |
| `DeleteApplicationCommand` | `DeleteApplicationCommandOutput` | `DeleteApplicationCommandHandler` |
| `HardDeleteApplicationCommand` | `HardDeleteApplicationCommandOutput` | `HardDeleteApplicationCommandHandler` |
| `CreateScopePermissionCommand` | `CreateScopePermissionCommandOutput` | `CreateScopePermissionCommandHandler` |
| `UpdateScopePermissionCommand` | `UpdateScopePermissionCommandOutput` | `UpdateScopePermissionCommandHandler` |
| `DeleteScopePermissionCommand` | `DeleteScopePermissionCommandOutput` | `DeleteScopePermissionCommandHandler` |
| `HardDeleteScopePermissionCommand` | `HardDeleteScopePermissionCommandOutput` | `HardDeleteScopePermissionCommandHandler` |
| `EnableTwoFactorAuthCommand` | `EnableTwoFactorAuthCommandOutput` | `EnableTwoFactorAuthCommandHandler` |
| `ConfirmTwoFactorAuthCommand` | `ConfirmTwoFactorAuthCommandOutput` | `ConfirmTwoFactorAuthCommandHandler` |
| `VerifyTwoFactorAuthCommand` | `VerifyTwoFactorAuthCommandOutput` | `VerifyTwoFactorAuthCommandHandler` |
| `DisableTwoFactorAuthCommand` | `DisableTwoFactorAuthCommandOutput` | `DisableTwoFactorAuthCommandHandler` |
| `RegenerateRecoveryCodesCommand` | `RegenerateRecoveryCodesCommandOutput` | `RegenerateRecoveryCodesCommandHandler` |

That is 36 registrations total, spanning what is currently lines 106–216 and 266–300 of `Startup.cs` (line numbers will shift once earlier edits in this task land — match by the command/handler names in the table, not by line number). Leave every `IValidator<...>` registration line exactly as it is, and leave every explanatory `//` comment above a registration in place, immediately above its rewritten line.

- [ ] **Step 4: Verify the whole solution builds**

Run: `dotnet build src/ArturRios.Heimdall.sln`
Expected: Build succeeds with no errors. If a command/handler pair was missed or mistyped, this fails with a clear generic-type or "type not found" error naming the exact line.

- [ ] **Step 5: Run the full existing suite to check for regressions**

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"`
Expected: PASS — every existing unit test still passes (no handler internals changed, only DI registration).

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"`
Expected: PASS — every existing functional test still passes. This also proves `IActorAccessor`/`IAuditLogWriter` resolve correctly through the full DI container (a missing registration would surface here as a `DI` resolution exception on the first authenticated write).

- [ ] **Step 6: Commit**

```bash
git add src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs
git commit -m "feat: wire AuditingCommandHandler into every command handler registration"
```

---

### Task 7: Functional test proving the audit trail is written

**Files:**
- Create: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/AuditLoggingTests.cs`

**Interfaces:**
- Consumes: `PostgresFixture`, `WebApiTest<Program>`, `TestTokens`, `Gateway.PostAsync<T>` (existing functional test support, see `ApplicationControllerCreateTests.cs`), `db.CreateContext().AuditLogs` (`AppDbContext`, Task 2).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Presentation/ArturRios.Heimdall.WebApi.Tests/AuditLoggingTests.cs
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for NFR-09 (audit logging): proves a successful write produces one AuditLog row
// carrying the acting caller, and that an anonymous write produces one with no actor.
[Collection(nameof(FunctionalCollection))]
public class AuditLoggingTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static CreateApplicationCommand Command(Guid ownerId, string? name = null) => new()
    {
        Name = name ?? $"app-{Guid.NewGuid():N}", OwnerId = ownerId
    };

    private static string ApplicationsRoute(Guid scopeId) => $"/api/scopes/{scopeId}/applications";

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedSystemAdminAsync()
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Root",
            Email = $"root-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.SystemAdmin, EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    [FunctionalFact]
    public async Task GivenAuthenticatedSystemAdmin_WhenCreatingApplication_ThenAuditLogRowCarriesActor()
    {
        // Given
        var scope = await SeedScopeAsync();
        var admin = await SeedSystemAdminAsync();
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));
        var command = Command(admin.PublicId);

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            ApplicationsRoute(scope.PublicId), command);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Then
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == nameof(CreateApplicationCommand)
                               && a.TargetId == response.Body!.Data!.Id);
        Assert.Equal(admin.PublicId, entry.ActorPersonId);
        Assert.Equal((int)Roles.SystemAdmin, entry.ActorRole);
    }

    [FunctionalFact]
    public async Task GivenRejectedCommand_WhenPostingWithNoScope_ThenNoAuditLogRowIsWritten()
    {
        // Given a scope id nobody holds — the write is rejected before anything is created
        var admin = await SeedSystemAdminAsync();
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            ApplicationsRoute(Guid.NewGuid()), Command(admin.PublicId));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Then
        await using var context = db.CreateContext();
        Assert.False(await context.AuditLogs.AnyAsync(a => a.Action == nameof(CreateApplicationCommand)
                                                             && a.ActorPersonId == admin.PublicId));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional&FullyQualifiedName~AuditLoggingTests"`
Expected: FAIL if Task 6 is incomplete or not yet committed on this branch; PASS is also an acceptable outcome here if Task 6 already landed correctly — in that case skip to Step 4 and just confirm the run is green.

- [ ] **Step 3: Fix any failure**

If the first test fails because `entry` is not found, re-check Task 6 Step 3 covered `CreateApplicationCommand` correctly and Task 3's `HttpContextActorAccessor` is registered. If the second test fails because a row *was* written, check `AuditingCommandHandler.HandleAsync` only calls the writer when `result.Success` is `true`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional&FullyQualifiedName~AuditLoggingTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the full suite one more time**

Run: `dotnet test src/ArturRios.Heimdall.sln`
Expected: PASS — everything, unit and functional, green.

- [ ] **Step 6: Commit**

```bash
git add tests/Presentation/ArturRios.Heimdall.WebApi.Tests/AuditLoggingTests.cs
git commit -m "test: cover NFR-09 audit logging end-to-end"
```

---

### Task 8: README update and pull request

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update the Platform table row**

In `README.md`, find the Platform table row:

```
| Audit logging for write operations (NFR-09) | ⬜ | — |
```

Replace it with (substituting the real issue number from Task 1, Step 1):

```
| Audit logging for write operations (NFR-09) | ✅ | [#<issue-number>](https://github.com/artur-rios/heimdall-api/issues/<issue-number>) |
```

- [ ] **Step 2: Remove the now-stale explanatory paragraph**

Directly below the Platform table, remove this paragraph (it exists only to explain why NFR-09 was outstanding):

```
One cross-cutting requirement is deliberately outstanding rather than forgotten:

- **NFR-09 (audit logging).** Write handlers currently produce no audit entries; the Serilog setup
  covers request/startup logging only. Every use case merged so far ships without it, so it is
  tracked here as one platform item rather than being retro-fitted per use case.
```

Leave the following paragraph (`**Email delivery** closed with UC-12...`) exactly as it is.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: mark NFR-09 audit logging done"
```

- [ ] **Step 4: Push the branch and open the pull request**

```bash
git push -u origin feature/nfr-09-audit-logging-for-write-operations
gh pr create \
  --title "feat: audit logging for write operations (NFR-09)" \
  --body "Closes #<issue-number>

Adds an AuditLog entity and an AuditingCommandHandler decorator wrapping every registered command handler, so every successful write produces one audit trail entry. See docs/superpowers/specs/2026-08-11-nfr-09-audit-logging-design.md for the design.

## Test plan
- [x] dotnet build src/ArturRios.Heimdall.sln
- [x] dotnet test src/ArturRios.Heimdall.sln --filter \"Category=Unit\"
- [x] dotnet test src/ArturRios.Heimdall.sln --filter \"Category=Functional\""
```

- [ ] **Step 5: Wait for human review and merge**

Per `docs/requirements/Development Workflow Document.md` Step 7, this PR is reviewed and merged by a human — do not self-approve or merge it. After merge: delete the feature branch, and set the GitHub issue's `Status` to **Done** and close it (the `Closes #<issue-number>` reference in the PR body does this automatically on merge — confirm the board reflects it).

---

## Out of scope (carried from the design doc)

- Exposing audit logs through a read endpoint.
- Logging failed/rejected write attempts.
- Retention/archival policy for the `audit_log` table.
- Capturing before/after field-level diffs.
