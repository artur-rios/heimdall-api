# NFR-09 Audit Logging — Design

Date: 2026-08-11

## 1. Purpose

`NFR-09` (System Requirements Document): *"All write operations shall produce audit log entries."*
Write handlers currently produce no audit trail — Serilog covers request/startup logging only. This
design adds a queryable, append-only audit trail for every command handler, without touching the
internals of any existing handler.

## 2. Constraint: no pipeline-behavior hook

`ArturRios.Mediator` (the CQRS library this project uses in place of MediatR) exposes only
`ICommandHandlerAsync<TCommand, TOutput>` resolved via plain DI — there is no
`IPipelineBehavior<T>` equivalent to hook cross-cutting concerns onto. The interception point has
to be the DI registration itself: wrap each concrete handler in a decorator that implements the same
`ICommandHandlerAsync<TCommand, TOutput>` interface.

## 3. `AuditLog` entity

`src/Domain/ArturRios.Heimdall.Domain/Entities/AuditLog.cs`, following the existing entity
conventions (`PublicId: Guid` as the external identifier, `Entity` base class):

```csharp
public class AuditLog : Entity
{
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public Guid? ActorPersonId { get; set; }
    public int? ActorRole { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid? TargetId { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

- **Append-only.** No `IsDeleted`, no `UpdatedAt` — audit entries are never modified after creation.
- **`ActorPersonId`/`ActorRole` are nullable.** Some writes have no authenticated actor (e.g.
  `PasswordRecoveryCommand`, which anyone can call anonymously).
- **`Action`** is the command's CLR type name (e.g. `"CreateApplicationCommand"`), not a hand-authored
  string — self-maintaining as commands are added or renamed.
- **`TargetId`** is best-effort: reflection looks for a public `Guid`-typed property named `Id` or
  `PublicId` on the command's output object and copies it if found; otherwise `null`. This avoids
  requiring every output DTO to implement a marker interface.
- **No raw command payload is stored.** Commands can carry passwords, tokens, and secrets
  (`CreateUserCommand`, `ResetPasswordCommand`, `EnableTwoFactorAuthCommand`); persisting them
  verbatim would leak secrets into the audit table. The four fields above are the full contents of an
  entry.
- **Only successful writes are logged.** A command that fails validation or authorization never
  mutated anything, so it produces no entry — this keeps the table free of routine rejection noise.

Table: `audit_log`, mapped via `EntityMaps/AuditLogDbMap.cs` following the existing per-entity
static-class convention (`ToTable`, `HasKey`, `HasIndex(x => x.PublicId).IsUnique()`,
`Property(x => x.CreatedAt).HasDefaultValueSql("now()")`). One EF Core migration adds the table.

## 4. Decorator

`src/Application/ArturRios.Heimdall.Command/Auditing/AuditingCommandHandler.cs`:

```csharp
public class AuditingCommandHandler<TCommand, TOutput>(
    ICommandHandlerAsync<TCommand, TOutput> inner,
    IAuditLogWriter auditLogWriter)
    : ICommandHandlerAsync<TCommand, TOutput>
{
    public async Task<DataOutput<TOutput?>> HandleAsync(TCommand command)
    {
        var result = await inner.HandleAsync(command);

        if (result.Success)
        {
            await auditLogWriter.WriteAsync(typeof(TCommand).Name, ResolveTargetId(result.Data));
        }

        return result;
    }

    private static Guid? ResolveTargetId(TOutput? output) { /* reflection over Id/PublicId */ }
}
```

**Layering constraint.** `IdentityUser` — the type `HttpContext.GetUser<IdentityUser>()` returns —
is defined in `ArturRios.Heimdall.WebApi.Security` (Presentation layer). `AuditingCommandHandler`
lives in `ArturRios.Heimdall.Command` (Application layer), which must not reference Presentation
types. The actor is therefore read through a new abstraction instead of `HttpContext` directly:

```csharp
// ArturRios.Heimdall.Shared/Security/IActorAccessor.cs
public interface IActorAccessor
{
    Guid? ActorPersonId { get; }
    int? ActorRole { get; }
}
```

`IAuditLogWriter` (new interface + implementation in `ArturRios.Heimdall.Command`'s `Auditing`
folder) depends on `IActorAccessor` and persists an `AuditLog` row through
`IAsyncRepository<AuditLog>`. The implementation, `HttpContextActorAccessor`, lives in
`ArturRios.Heimdall.WebApi.Security` next to `IdentityUser` and `ActorExtensions`, and reads
`IHttpContextAccessor.HttpContext?.GetUser<IdentityUser>()` — the same accessor
`ActorExtensions.ApplyActor` already uses. Reading the actor this way rather than from the command
means every handler is covered uniformly, including the ones with no `IActorScoped` field at all
(`ResendVerificationEmailCommand`, `GoogleSignOutCommand`, `PasswordRecoveryCommand`, ...). When
there is no authenticated user on the context (anonymous endpoints), `ActorPersonId`/`ActorRole`
are `null`.

A failure inside `auditLogWriter.WriteAsync` must never fail the original write — it is caught and
logged via `ILogger<AuditingCommandHandler<TCommand, TOutput>>` (bridged to Serilog through
`Host.UseSerilog()`) rather than rethrown, so an audit-logging outage cannot take down the API's
actual functionality.

`ArturRios.Heimdall.Command` gains package references to
`Microsoft.Extensions.DependencyInjection.Abstractions` (for the `IServiceCollection` extension
method) and `Microsoft.Extensions.Logging.Abstractions` (for `ILogger<T>`). `Startup.AddDependencies`
registers `IHttpContextAccessor` (`AddHttpContextAccessor()` — available for free since
`ArturRios.Heimdall.WebApi` already uses the `Microsoft.NET.Sdk.Web` SDK) and
`IActorAccessor`/`IAuditLogWriter`.

## 5. DI registration

Every one of the 36 `.AddScoped<ICommandHandlerAsync<TCommand, TOutput>, THandler>()` calls in
`Startup.cs` is replaced by a call to a new extension method:

```csharp
public static IServiceCollection AddAuditedCommandHandler<TCommand, TOutput, THandler>(
    this IServiceCollection services)
    where THandler : class, ICommandHandlerAsync<TCommand, TOutput>
{
    services.AddScoped<THandler>();
    services.AddScoped<ICommandHandlerAsync<TCommand, TOutput>>(sp =>
        new AuditingCommandHandler<TCommand, TOutput>(
            sp.GetRequiredService<THandler>(),
            sp.GetRequiredService<IAuditLogWriter>()));

    return services;
}
```

This is a mechanical, one-line-per-call-site change — no handler's internal logic changes. Every
registered command handler is covered, including the auth-flow ones (login, password reset, email
verification, 2FA enable/confirm/verify/disable, Google sign-in/out), per NFR-09's literal
"all write operations."

## 6. Tests

- **Unit** (`tests/Application/ArturRios.Heimdall.Command.Tests/Auditing/AuditingCommandHandlerTests.cs`):
  `[UnitFact]` cases — successful inner result triggers exactly one `IAuditLogWriter.WriteAsync` call
  with the right action name and resolved target id; a failed inner result triggers none; a writer
  exception is swallowed and the original successful result is still returned.
- **Functional**: extend one existing functional test (e.g. `ApplicationControllerCreateTests`) with
  an assertion that a matching `audit_log` row exists after a successful `POST`, using the same
  `db.CreateContext()` pattern already used to assert the primary entity.

## 7. Delivery

New GitHub issue, "Audit logging for write operations (NFR-09)", tracked in the README's Platform
table (replacing the current `—` issue link). Branch
`feature/nfr-09-audit-logging-for-write-operations`. One PR covering: migration, entity, decorator,
`IAuditLogWriter`, the 36 DI registration edits, and the tests above.

## 8. Out of scope

- Exposing audit logs through a read endpoint (`GET /api/audit-logs` or similar) — NFR-09 only
  requires entries to be produced, not queried via the API. A future use case can add that.
- Logging failed/rejected write attempts.
- Retention/archival policy for the `audit_log` table.
- Capturing before/after field-level diffs — only actor, action, and target id.
- **Known limitation: no target identification for anonymous-actor flows.** Five command handlers —
  `LoginCommand`, `PasswordRecoveryCommand`, `ResetPasswordCommand`, `VerifyEmailCommand`, and
  `GoogleSignInCommand` — run on anonymous endpoints (no authenticated actor), and their output DTOs
  carry no `Id`/`PublicId` property for `AuditingCommandHandler`'s reflection-based
  `ResolveTargetId` to pick up. Their audit rows therefore carry only `(action, created_at)`, with
  both actor and target null — no forensic identification of which account was affected. This is a
  real gap for exactly the flows most worth auditing (password resets, logins), discovered during
  implementation rather than planned for. Fixing it — e.g. adding the affected person's `PublicId`
  to `ResetPasswordCommandOutput`/`VerifyEmailCommandOutput`, which are currently empty marker
  classes, and to the other three outputs similarly — is additive work, better scoped as its own
  follow-up than done reactively in this fix wave. Stated here plainly as a known limitation, not a
  TODO to silently drop.
