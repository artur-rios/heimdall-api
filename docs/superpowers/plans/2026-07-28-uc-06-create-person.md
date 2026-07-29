# UC-06 Create Person Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement UC-06 (Create Person) — three creation paths (a `User` in a scope, a `ScopeAdmin`/`SystemAdmin` without a scope, and a new `ScopeAdmin` co-owner of a scope) — each hashing the password, issuing a stubbed email-verification token, and returning the created person.

**Architecture:** CQRS write flow mirroring UC-01. One `PersonController` exposes all three endpoints; three commands/validators/handlers share one output type, one `PersonMessages`/`PersonMessageMap` pair, and one `IEmailVerificationService`. Handlers return `DataOutput<T>` and never throw. Scope-ownership authorization (AF-06e) is enforced in the handler by a DB lookup using the acting user the controller reads from `HttpContext.Items["User"]`.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core (PostgreSQL), FluentValidation, ArturRios.Mediator / .Output / .Data.Relational.Core / .Util / .Util.WebApi; xUnit + Moq + Bogus + Testcontainers for tests.

## Global Constraints

- **No schema change / no EF migration** — `person`, `scope_user`, `scope_owner`, `email_verification_token` tables and maps already exist from `InitialCreate`.
- **Identifiers:** inputs/outputs/routes use `PublicId` (GUID); joins/FKs use internal `Id` (bigint). Never expose or accept internal `Id`. Never return `PasswordHash`/`Salt`.
- **Handlers return `DataOutput<T>`, never throw.** Failures are errors carrying a canonical `PersonMessages` value.
- **Roles:** `SystemAdmin = 1`, `ScopeAdmin = 2`, `User = 3`; the seeder guarantees `Role.Id == (long)Roles`.
- **Acting user:** the auth middleware attaches an `ArturRios.Util.WebApi.Security.Records.AuthenticatedUser(int Id, int Role)` to `HttpContext.Items["User"]`; the `Id` claim is the person's internal `Id`.
- **Password hashing:** `ArturRios.Util.Hashing.Hash.EncodeWithRandomSalt(password, out byte[] salt)` returns the `byte[]` hash.
- **Tests:** unit tests use `AsyncFakeRepository<T>` (one instance = reader + writer), Moq for validators/services, Bogus optional; functional tests derive from `WebApiTest<Program>`, join `[Collection(nameof(FunctionalCollection))]`, authorize via `TestTokens`, and assert response **and** DB state via `db.CreateContext()`. GWT naming, `// Given / // When / // Then`. Attributes `[UnitFact]` / `[FunctionalFact]`.
- **Run filters:** `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` and `"Category=Functional"`.

---

## File Structure

**New — production:**
- `src/Application/ArturRios.IdentityManager.Command/Services/IEmailVerificationSender.cs`
- `src/Application/ArturRios.IdentityManager.Command/Services/IEmailVerificationService.cs`
- `src/Application/ArturRios.IdentityManager.Command/Services/EmailVerificationService.cs`
- `src/Application/ArturRios.IdentityManager.Command/Services/EmailVerificationOptions.cs`
- `src/Application/ArturRios.IdentityManager.Command/Input/CreateAdminCommand.cs`
- `src/Application/ArturRios.IdentityManager.Command/Input/CreateUserCommand.cs`
- `src/Application/ArturRios.IdentityManager.Command/Input/CreateScopeOwnerCommand.cs`
- `src/Application/ArturRios.IdentityManager.Command/Input/Validation/CreateAdminCommandValidator.cs`
- `src/Application/ArturRios.IdentityManager.Command/Input/Validation/CreateUserCommandValidator.cs`
- `src/Application/ArturRios.IdentityManager.Command/Input/Validation/CreateScopeOwnerCommandValidator.cs`
- `src/Application/ArturRios.IdentityManager.Command/Handlers/CreateAdminCommandHandler.cs`
- `src/Application/ArturRios.IdentityManager.Command/Handlers/CreateUserCommandHandler.cs`
- `src/Application/ArturRios.IdentityManager.Command/Handlers/CreateScopeOwnerCommandHandler.cs`
- `src/Application/ArturRios.IdentityManager.Command/Output/CreatePersonCommandOutput.cs`
- `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessages.cs`
- `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessageMap.cs`
- `src/Presentation/ArturRios.IdentityManager.WebApi/Security/LoggingEmailVerificationSender.cs`
- `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs`

**Modified — production:**
- `src/Application/ArturRios.IdentityManager.Command/ArturRios.IdentityManager.Command.csproj` (add `ArturRios.Util`)
- `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs` (DI)

**New — tests:**
- `tests/Application/ArturRios.IdentityManager.Command.Tests/EmailVerificationServiceTests.cs`
- `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateAdminCommandHandlerTests.cs`
- `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateAdminCommandValidatorTests.cs`
- `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateUserCommandHandlerTests.cs`
- `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateScopeOwnerCommandHandlerTests.cs`
- `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerCreateAdminTests.cs`
- `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerCreateUserTests.cs`
- `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerCreateScopeOwnerTests.cs`

**Modified — tests:**
- `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/Support/TestTokens.cs` (add id-specific overload)

---

## Task 1: Email verification service + stub sender

**Files:**
- Modify: `src/Application/ArturRios.IdentityManager.Command/ArturRios.IdentityManager.Command.csproj`
- Create: `src/Application/ArturRios.IdentityManager.Command/Services/IEmailVerificationSender.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Services/EmailVerificationOptions.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Services/IEmailVerificationService.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Services/EmailVerificationService.cs`
- Create: `src/Presentation/ArturRios.IdentityManager.WebApi/Security/LoggingEmailVerificationSender.cs`
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`
- Test: `tests/Application/ArturRios.IdentityManager.Command.Tests/EmailVerificationServiceTests.cs`

**Interfaces:**
- Produces:
  - `interface IEmailVerificationSender { Task SendAsync(string email, string token); }`
  - `interface IEmailVerificationService { Task IssueAndSendAsync(Person person); }`
  - `class EmailVerificationService(IAsyncRepository<EmailVerificationToken> tokenWriter, IEmailVerificationSender sender, EmailVerificationOptions options) : IEmailVerificationService`
  - `class EmailVerificationOptions { TimeSpan TokenLifetime {get; init;} = 24h; static EmailVerificationOptions FromEnvironment(); }`

- [ ] **Step 1: Add the ArturRios.Util package reference to the Command project**

In `src/Application/ArturRios.IdentityManager.Command/ArturRios.IdentityManager.Command.csproj`, add inside the package `<ItemGroup>` (alongside the existing `ArturRios.*` references):

```xml
      <PackageReference Include="ArturRios.Util" Version="1.4.2" />
```

- [ ] **Step 2: Create the sender abstraction and options**

Create `Services/IEmailVerificationSender.cs`:

```csharp
namespace ArturRios.IdentityManager.Command.Services;

/// <summary>
///     Delivers an email-verification token to a person's address (UC-06 / FR-EV-01). The concrete
///     delivery mechanism is an infrastructure concern; UC-06 ships a logging stub.
/// </summary>
public interface IEmailVerificationSender
{
    Task SendAsync(string email, string token);
}
```

Create `Services/EmailVerificationOptions.cs`:

```csharp
namespace ArturRios.IdentityManager.Command.Services;

/// <summary>
///     Settings for email-verification token issuance. <see cref="TokenLifetime" /> is read from the
///     environment (seconds) with a 24-hour default.
/// </summary>
public class EmailVerificationOptions
{
    private const string LifetimeVariable = "IDENTITY_MANAGER_EMAIL_VERIFICATION_TOKEN_EXPIRATION_IN_SECONDS";
    private const double DefaultLifetimeSeconds = 86400;

    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromSeconds(DefaultLifetimeSeconds);

    public static EmailVerificationOptions FromEnvironment()
    {
        var seconds = double.TryParse(Environment.GetEnvironmentVariable(LifetimeVariable), out var configured)
            ? configured
            : DefaultLifetimeSeconds;

        return new EmailVerificationOptions { TokenLifetime = TimeSpan.FromSeconds(seconds) };
    }
}
```

- [ ] **Step 3: Write the failing test**

Create `tests/Application/ArturRios.IdentityManager.Command.Tests/EmailVerificationServiceTests.cs`:

```csharp
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for EmailVerificationService (UC-06, FR-EV-01/02): a token is persisted for the person
// with a future expiry and Used=false, then handed to the sender.
public class EmailVerificationServiceTests
{
    [UnitFact]
    public async Task GivenAPerson_WhenIssuingAndSending_ThenTokenIsPersistedAndSent()
    {
        // Given
        var tokens = new AsyncFakeRepository<EmailVerificationToken>();
        var sender = new Mock<IEmailVerificationSender>();
        var options = new EmailVerificationOptions { TokenLifetime = TimeSpan.FromHours(1) };
        var service = new EmailVerificationService(tokens, sender.Object, options);
        var person = new Person { Email = "user@test.local" };
        await new AsyncFakeRepository<Person>().CreateAsync(person); // assigns person.Id

        // When
        await service.IssueAndSendAsync(person);

        // Then — a token was stored for the person, unused, expiring in the future
        var stored = (await tokens.GetAllAsync()).Data!.Single();
        Assert.Equal(person.Id, stored.PersonId);
        Assert.False(stored.Used);
        Assert.False(string.IsNullOrWhiteSpace(stored.Token));
        Assert.True(stored.ExpiresAt > DateTime.UtcNow);

        // Then — the sender received the person's email and the same token
        sender.Verify(s => s.SendAsync(person.Email, stored.Token), Times.Once);
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~EmailVerificationServiceTests"`
Expected: FAIL — `EmailVerificationService` / `IEmailVerificationService` do not exist (compile error).

- [ ] **Step 5: Create the service interface and implementation**

Create `Services/IEmailVerificationService.cs`:

```csharp
using ArturRios.IdentityManager.Domain.Entities;

namespace ArturRios.IdentityManager.Command.Services;

/// <summary>
///     Issues, persists, and dispatches an email-verification token for a person (UC-06 /
///     FR-EV-01/02). Shared by every Create Person path so token logic is not duplicated.
/// </summary>
public interface IEmailVerificationService
{
    Task IssueAndSendAsync(Person person);
}
```

Create `Services/EmailVerificationService.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.Util.Random;

namespace ArturRios.IdentityManager.Command.Services;

/// <summary>
///     Default <see cref="IEmailVerificationService" />: builds a random, time-limited
///     <see cref="EmailVerificationToken" />, persists it, then delegates delivery to the configured
///     <see cref="IEmailVerificationSender" />. A send failure does not undo the created person — the
///     persisted token can be re-sent later (UC-15).
/// </summary>
public class EmailVerificationService(
    IAsyncRepository<EmailVerificationToken> tokenWriter,
    IEmailVerificationSender sender,
    EmailVerificationOptions options)
    : IEmailVerificationService
{
    public async Task IssueAndSendAsync(Person person)
    {
        var token = CustomRandom.Text(new RandomStringOptions
        {
            Length = 48,
            IncludeLowercase = true,
            IncludeUppercase = true,
            IncludeDigits = true,
            IncludeSpecialCharacters = false
        });

        await tokenWriter.CreateAsync(new EmailVerificationToken
        {
            PersonId = person.Id,
            Token = token,
            ExpiresAt = DateTime.UtcNow.Add(options.TokenLifetime),
            Used = false
        });

        await sender.SendAsync(person.Email, token);
    }
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~EmailVerificationServiceTests"`
Expected: PASS.

- [ ] **Step 7: Create the logging stub sender in the Web API project**

Create `src/Presentation/ArturRios.IdentityManager.WebApi/Security/LoggingEmailVerificationSender.cs`:

```csharp
using ArturRios.IdentityManager.Command.Services;

namespace ArturRios.IdentityManager.WebApi.Security;

/// <summary>
///     UC-06 stub for <see cref="IEmailVerificationSender" />: logs the recipient and token instead of
///     delivering a real email. Real delivery is deferred to a dedicated email-infrastructure change.
/// </summary>
public class LoggingEmailVerificationSender(ILogger<LoggingEmailVerificationSender> logger)
    : IEmailVerificationSender
{
    public Task SendAsync(string email, string token)
    {
        logger.LogInformation("Email verification token issued for {Email}: {Token}", email, token);

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 8: Register the service, sender, and options in DI**

In `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`, add these `using`s at the top:

```csharp
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.WebApi.Security;
```

In `AddDependencies()`, immediately before `Builder.Services.AddSingleton(MasterUserOptions.FromEnvironment());`, add:

```csharp
        Builder.Services.AddSingleton(EmailVerificationOptions.FromEnvironment());
        Builder.Services.AddScoped<IEmailVerificationSender, LoggingEmailVerificationSender>();
        Builder.Services.AddScoped<IEmailVerificationService, EmailVerificationService>();
```

- [ ] **Step 9: Build to verify wiring compiles**

Run: `dotnet build src/ArturRios.IdentityManager.sln`
Expected: Build succeeded.

- [ ] **Step 10: Commit**

```bash
git add src/Application/ArturRios.IdentityManager.Command tests/Application/ArturRios.IdentityManager.Command.Tests/EmailVerificationServiceTests.cs src/Presentation/ArturRios.IdentityManager.WebApi/Security src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs
git commit -m "feat: add UC-06 email verification service and logging sender"
```

---

## Task 2: Shared output, messages, and Create Admin handler (path b)

**Files:**
- Create: `src/Application/ArturRios.IdentityManager.Command/Output/CreatePersonCommandOutput.cs`
- Create: `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessages.cs`
- Create: `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessageMap.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Input/CreateAdminCommand.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Input/Validation/CreateAdminCommandValidator.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Handlers/CreateAdminCommandHandler.cs`
- Test: `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateAdminCommandValidatorTests.cs`
- Test: `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateAdminCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `IEmailVerificationService` (Task 1).
- Produces:
  - `class CreatePersonCommandOutput : CommandOutput { Guid Id; string Name; string Email; int Role; bool EmailVerified; Guid? ScopeId; DateTime CreatedAt; }`
  - `static class PersonMessages` (const strings listed below) and `static class PersonMessageMap { IReadOnlyDictionary<string,int> StatusCodes; }`
  - `class CreateAdminCommand : BaseCommand { string Name; string Email; string Password; int Role; }`
  - `class CreateAdminCommandHandler(IValidator<CreateAdminCommand>, IAsyncReadOnlyRepository<Person> personReader, IAsyncRepository<Person> personWriter, IEmailVerificationService) : ICommandHandlerAsync<CreateAdminCommand, CreatePersonCommandOutput>`

- [ ] **Step 1: Create the shared output type**

Create `Output/CreatePersonCommandOutput.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     The person created by any UC-06 path. Exposes only external-facing identifiers; never
///     <c>PasswordHash</c> / <c>Salt</c>. <see cref="ScopeId" /> is populated for paths a and c and
///     <c>null</c> for path b (admins have no scope association at creation).
/// </summary>
public class CreatePersonCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the created person.</summary>
    public Guid Id { get; set; }

    /// <summary>Full name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Email address.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Assigned role value (see <c>Roles</c>).</summary>
    public int Role { get; set; }

    /// <summary>Whether the email is verified (always <c>false</c> at creation).</summary>
    public bool EmailVerified { get; set; }

    /// <summary>Public identifier of the associated scope (paths a and c); <c>null</c> for path b.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }
}
```

- [ ] **Step 2: Create the messages and status map**

Create `Shared/Messages/PersonMessages.cs`:

```csharp
namespace ArturRios.IdentityManager.Shared.Messages;

/// <summary>
///     Canonical messages produced by the person use cases. Each is mapped to an HTTP status code in
///     <see cref="PersonMessageMap" />.
/// </summary>
public static class PersonMessages
{
    /// <summary>UC-06 success: the person was created.</summary>
    public const string PersonCreatedSuccessfully = "Person created successfully.";

    /// <summary>AF-06a: the email is already in use (within the scope for Users, system-wide for admins).</summary>
    public const string EmailAlreadyExists = "A person with this email already exists.";

    /// <summary>AF-06b: the target scope does not exist or is logically deleted.</summary>
    public const string ScopeNotFound = "Scope not found.";

    /// <summary>AF-06e: a Scope Admin acted on a scope they do not own.</summary>
    public const string NotScopeOwner = "You are not an owner of the target scope.";

    /// <summary>AF-06d: name was not supplied.</summary>
    public const string NameRequired = "Name is required.";

    /// <summary>AF-06d: name exceeds the maximum length.</summary>
    public const string NameTooLong = "Name must be at most 200 characters.";

    /// <summary>AF-06d: email was not supplied.</summary>
    public const string EmailRequired = "Email is required.";

    /// <summary>AF-06d: email is not a valid address.</summary>
    public const string EmailInvalid = "Email is not valid.";

    /// <summary>AF-06d: password was not supplied.</summary>
    public const string PasswordRequired = "Password is required.";

    /// <summary>AF-06d: password is shorter than the minimum length.</summary>
    public const string PasswordTooShort = "Password must be at least 8 characters.";

    /// <summary>AF-06d: the requested role is not ScopeAdmin or SystemAdmin (path b).</summary>
    public const string InvalidRole = "Role must be ScopeAdmin or SystemAdmin.";
}
```

Create `Shared/Messages/PersonMessageMap.cs`:

```csharp
using ArturRios.Util.Http;

namespace ArturRios.IdentityManager.Shared.Messages;

/// <summary>
///     Maps each <see cref="PersonMessages" /> value to its HTTP status code, following the UC-06
///     flows. Passed to the response resolver.
/// </summary>
public static class PersonMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        // UC-06 main flow — person created.
        [PersonMessages.PersonCreatedSuccessfully] = HttpStatusCodes.Created,
        // AF-06a — email already exists.
        [PersonMessages.EmailAlreadyExists] = HttpStatusCodes.Conflict,
        // AF-06b — scope not found.
        [PersonMessages.ScopeNotFound] = HttpStatusCodes.NotFound,
        // AF-06e — actor is not an owner of the target scope.
        [PersonMessages.NotScopeOwner] = HttpStatusCodes.Forbidden,
        // AF-06d — invalid input.
        [PersonMessages.NameRequired] = HttpStatusCodes.BadRequest,
        [PersonMessages.NameTooLong] = HttpStatusCodes.BadRequest,
        [PersonMessages.EmailRequired] = HttpStatusCodes.BadRequest,
        [PersonMessages.EmailInvalid] = HttpStatusCodes.BadRequest,
        [PersonMessages.PasswordRequired] = HttpStatusCodes.BadRequest,
        [PersonMessages.PasswordTooShort] = HttpStatusCodes.BadRequest,
        [PersonMessages.InvalidRole] = HttpStatusCodes.BadRequest
    };
}
```

- [ ] **Step 3: Create the command**

Create `Input/CreateAdminCommand.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to create a <c>ScopeAdmin</c> or <c>SystemAdmin</c> person without any scope
///     association (UC-06 path b). <see cref="Role" /> is the <c>Roles</c> enum value and must be
///     <c>SystemAdmin</c> or <c>ScopeAdmin</c>.
/// </summary>
public class CreateAdminCommand : BaseCommand
{
    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public int Role { get; set; }
}
```

- [ ] **Step 4: Write the failing validator test**

Create `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateAdminCommandValidatorTests.cs`:

```csharp
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Input.Validation;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for CreateAdminCommandValidator (UC-06 path b, AF-06d).
public class CreateAdminCommandValidatorTests
{
    private static CreateAdminCommand Valid() => new()
    {
        Name = "Admin",
        Email = "admin@test.local",
        Password = "Str0ngPass!",
        Role = (int)Roles.ScopeAdmin
    };

    [UnitFact]
    public async Task GivenValidCommand_WhenValidating_ThenNoErrors()
    {
        var result = await new CreateAdminCommandValidator().ValidateAsync(Valid());
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenEmptyPassword_WhenValidating_ThenPasswordRequiredError()
    {
        var command = Valid();
        command.Password = "";
        var result = await new CreateAdminCommandValidator().ValidateAsync(command);
        Assert.Contains(result.Errors, e => e.ErrorMessage == PersonMessages.PasswordRequired);
    }

    [UnitFact]
    public async Task GivenUserRole_WhenValidating_ThenInvalidRoleError()
    {
        var command = Valid();
        command.Role = (int)Roles.User;
        var result = await new CreateAdminCommandValidator().ValidateAsync(command);
        Assert.Contains(result.Errors, e => e.ErrorMessage == PersonMessages.InvalidRole);
    }

    [UnitFact]
    public async Task GivenInvalidEmail_WhenValidating_ThenEmailInvalidError()
    {
        var command = Valid();
        command.Email = "not-an-email";
        var result = await new CreateAdminCommandValidator().ValidateAsync(command);
        Assert.Contains(result.Errors, e => e.ErrorMessage == PersonMessages.EmailInvalid);
    }
}
```

- [ ] **Step 5: Run to verify it fails**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~CreateAdminCommandValidatorTests"`
Expected: FAIL — `CreateAdminCommandValidator` does not exist (compile error).

- [ ] **Step 6: Create the validator**

Create `Input/Validation/CreateAdminCommandValidator.cs`:

```csharp
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using FluentValidation;

namespace ArturRios.IdentityManager.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="CreateAdminCommand" /> (UC-06 path b, AF-06d). Business rules
///     (email uniqueness) are enforced by the handler.
/// </summary>
public class CreateAdminCommandValidator : AbstractValidator<CreateAdminCommand>
{
    public CreateAdminCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty().WithMessage(PersonMessages.NameRequired)
            .MaximumLength(200).WithMessage(PersonMessages.NameTooLong);

        RuleFor(command => command.Email)
            .NotEmpty().WithMessage(PersonMessages.EmailRequired)
            .EmailAddress().WithMessage(PersonMessages.EmailInvalid);

        RuleFor(command => command.Password)
            .NotEmpty().WithMessage(PersonMessages.PasswordRequired)
            .MinimumLength(8).WithMessage(PersonMessages.PasswordTooShort);

        RuleFor(command => command.Role)
            .Must(role => role == (int)Roles.SystemAdmin || role == (int)Roles.ScopeAdmin)
            .WithMessage(PersonMessages.InvalidRole);
    }
}
```

- [ ] **Step 7: Run to verify the validator tests pass**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~CreateAdminCommandValidatorTests"`
Expected: PASS.

- [ ] **Step 8: Write the failing handler test**

Create `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateAdminCommandHandlerTests.cs`:

```csharp
using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for CreateAdminCommandHandler (UC-06 path b): main flow + AF-06a (duplicate admin
// email system-wide). AF-06c (non-System-Admin) and AF-06d (invalid input) are functional/validator
// concerns.
public class CreateAdminCommandHandlerTests
{
    private static Mock<IValidator<CreateAdminCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<CreateAdminCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateAdminCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private static CreateAdminCommand Command(int role) => new()
    {
        Name = "Admin",
        Email = $"admin-{Guid.NewGuid():N}@test.local",
        Password = "Str0ngPass!",
        Role = role
    };

    [UnitFact]
    public async Task GivenUniqueEmail_WhenHandlingCreateAdmin_ThenScopeAdminIsCreatedWithoutJoinRow()
    {
        // Given
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateAdminCommandHandler(ValidValidator().Object, persons, persons, email.Object);
        var command = Command((int)Roles.ScopeAdmin);

        // When
        var output = await handler.HandleAsync(command);

        // Then — output
        Assert.True(output.Success);
        Assert.Equal((int)Roles.ScopeAdmin, output.Data!.Role);
        Assert.Null(output.Data.ScopeId);
        Assert.Contains(PersonMessages.PersonCreatedSuccessfully, output.Messages);

        // Then — a person was stored with RoleId=ScopeAdmin, EmailVerified=false, no membership/ownership
        var stored = (await persons.GetAllAsync()).Data!.Single();
        Assert.Equal((long)Roles.ScopeAdmin, stored.RoleId);
        Assert.False(stored.EmailVerified);
        Assert.NotEmpty(stored.PasswordHash);
        Assert.NotEmpty(stored.Salt);
        Assert.Null(stored.ScopeMembership);
        Assert.Empty(stored.ScopeOwnerships);

        // Then — a verification email was issued
        email.Verify(e => e.IssueAndSendAsync(It.IsAny<Person>()), Times.Once);
    }

    [UnitFact]
    public async Task GivenSystemAdminRole_WhenHandlingCreateAdmin_ThenSystemAdminIsCreated()
    {
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateAdminCommandHandler(ValidValidator().Object, persons, persons, email.Object);

        var output = await handler.HandleAsync(Command((int)Roles.SystemAdmin));

        Assert.True(output.Success);
        Assert.Equal((int)Roles.SystemAdmin, output.Data!.Role);
        Assert.Equal((long)Roles.SystemAdmin, (await persons.GetAllAsync()).Data!.Single().RoleId);
    }

    [UnitFact]
    public async Task GivenExistingAdminEmail_WhenHandlingCreateAdmin_ThenReturnsEmailAlreadyExists()
    {
        // Given an existing ScopeAdmin with the same email (AF-06a)
        var persons = new AsyncFakeRepository<Person>();
        var command = Command((int)Roles.ScopeAdmin);
        await persons.CreateAsync(new Person
        {
            Email = command.Email, RoleId = (long)Roles.ScopeAdmin, IsDeleted = false
        });
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateAdminCommandHandler(ValidValidator().Object, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
        email.Verify(e => e.IssueAndSendAsync(It.IsAny<Person>()), Times.Never);
    }
}
```

- [ ] **Step 9: Run to verify it fails**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~CreateAdminCommandHandlerTests"`
Expected: FAIL — `CreateAdminCommandHandler` does not exist (compile error).

- [ ] **Step 10: Create the handler**

Create `Handlers/CreateAdminCommandHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="CreateAdminCommand" /> (UC-06 path b): validates the request, verifies the
///     email is unique among admin persons system-wide (AF-06a), hashes the password, and creates a
///     <c>ScopeAdmin</c>/<c>SystemAdmin</c> with no scope association, then issues a verification
///     token. AF-06c (non-System-Admin) is enforced by the controller's role requirement.
/// </summary>
public class CreateAdminCommandHandler(
    IValidator<CreateAdminCommand> validator,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IEmailVerificationService emailVerification)
    : ICommandHandlerAsync<CreateAdminCommand, CreatePersonCommandOutput>
{
    public async Task<DataOutput<CreatePersonCommandOutput?>> HandleAsync(CreateAdminCommand command)
    {
        var output = DataOutput<CreatePersonCommandOutput?>.New;

        // AF-06d: validate input shape.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-06a: admin emails are unique system-wide.
        var emailTaken = await personReader.Query().AnyAsync(person =>
            !person.IsDeleted && person.Email == command.Email &&
            (person.RoleId == (long)Roles.SystemAdmin || person.RoleId == (long)Roles.ScopeAdmin));

        if (emailTaken)
        {
            return output.WithError(PersonMessages.EmailAlreadyExists);
        }

        // Create the admin person with no SCOPE_OWNER/SCOPE_USER row.
        var passwordHash = Hash.EncodeWithRandomSalt(command.Password, out var salt);

        var newPerson = new Person
        {
            Name = command.Name,
            Email = command.Email,
            PasswordHash = passwordHash,
            Salt = salt,
            RoleId = command.Role
        };

        var creation = await personWriter.CreateAsync(newPerson);

        if (!creation.Success)
        {
            return output.WithErrors(creation.Errors);
        }

        // FR-EV-01/02: issue and send the verification token.
        await emailVerification.IssueAndSendAsync(newPerson);

        return output
            .WithData(new CreatePersonCommandOutput
            {
                Id = newPerson.PublicId,
                Name = newPerson.Name,
                Email = newPerson.Email,
                Role = command.Role,
                EmailVerified = newPerson.EmailVerified,
                CreatedAt = newPerson.CreatedAt
            })
            .WithMessage(PersonMessages.PersonCreatedSuccessfully);
    }
}
```

- [ ] **Step 11: Run to verify the handler tests pass**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~CreateAdminCommandHandlerTests"`
Expected: PASS.

- [ ] **Step 12: Commit**

```bash
git add src/Application tests/Application/ArturRios.IdentityManager.Command.Tests/CreateAdminCommand*
git commit -m "feat: add UC-06 create admin handler (path b)"
```

---

## Task 3: Create Admin endpoint (path b) + functional tests

**Files:**
- Create: `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs`
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`
- Test: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerCreateAdminTests.cs`

**Interfaces:**
- Consumes: `CreateAdminCommand`, `CreatePersonCommandOutput`, `PersonMessageMap`, `CreateAdminCommandHandler` (Task 2).
- Produces: `PersonController` with `POST /api/persons` (`CreateAdmin`).

- [ ] **Step 1: Register the path-b validator and handler in DI**

In `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`, add these `using`s if not present:

```csharp
using ArturRios.IdentityManager.Command.Input.Validation;
```

(`ArturRios.IdentityManager.Command.Input`, `ArturRios.IdentityManager.Command.Handlers`, `ArturRios.IdentityManager.Command.Output`, `ArturRios.Mediator.Command.Interfaces`, and `FluentValidation` are already imported by the scope registrations.)

In `AddDependencies()`, after the last scope command-handler registration (the `HardDeleteScopeCommandHandler` line) and before `Builder.Services.AddScoped<QueryMediator>();`, add:

```csharp
        Builder.Services.AddScoped<IValidator<CreateAdminCommand>, CreateAdminCommandValidator>();
        Builder.Services
            .AddScoped<ICommandHandlerAsync<CreateAdminCommand, CreatePersonCommandOutput>, CreateAdminCommandHandler>();
```

- [ ] **Step 2: Create the controller with the path-b endpoint**

Create `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs`:

```csharp
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.IdentityManager.WebApi.Controllers;

[Route("api")]
public class PersonController(CommandMediator commandMediator) : Controller
{
    /// <summary>
    ///     Creates a <c>ScopeAdmin</c> or <c>SystemAdmin</c> person with no scope (UC-06 path b).
    ///     Restricted to System Admins (AF-06c).
    /// </summary>
    [HttpPost("persons")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<CreatePersonCommandOutput?>>> CreateAdmin(
        [FromBody] CreateAdminCommand command)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<CreateAdminCommand, CreatePersonCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }
}
```

- [ ] **Step 3: Write the failing functional tests**

Create `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerCreateAdminTests.cs`:

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.WebApi.Tests;

[Collection(nameof(FunctionalCollection))]
public class PersonControllerCreateAdminTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string UniqueEmail() => $"admin-{Guid.NewGuid():N}@test.local";

    private static CreateAdminCommand Command(string email, int role) => new()
    {
        Name = "Admin", Email = email, Password = "Str0ngPass!", Role = role
    };

    private async Task<Person> SeedAdminAsync(string email, Roles role)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Existing", Email = email,
            RoleId = (long)role, EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    [FunctionalFact]
    public async Task GivenSystemAdminAndValidScopeAdmin_WhenPostPersons_ThenCreated()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var email = UniqueEmail();

        // When
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            "/api/persons", Command(email, (int)Roles.ScopeAdmin));

        // Then — response
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(email, response.Body?.Data?.Email);
        Assert.Null(response.Body?.Data?.ScopeId);

        // Then — database state: person with RoleId=ScopeAdmin, EmailVerified=false, a token, no join rows
        await using var context = db.CreateContext();
        var person = await context.Persons.AsNoTracking().FirstAsync(p => p.Email == email);
        Assert.Equal((long)Roles.ScopeAdmin, person.RoleId);
        Assert.False(person.EmailVerified);
        Assert.NotEmpty(person.PasswordHash);
        Assert.True(await context.EmailVerificationTokens.AnyAsync(t => t.PersonId == person.Id));
        Assert.False(await context.ScopeUsers.AnyAsync(su => su.PersonId == person.Id));
        Assert.False(await context.ScopeOwners.AnyAsync(so => so.PersonId == person.Id));
    }

    [FunctionalFact]
    public async Task GivenDuplicateAdminEmail_WhenPostPersons_ThenConflict()
    {
        var existing = await SeedAdminAsync(UniqueEmail(), Roles.ScopeAdmin);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            "/api/persons", Command(existing.Email, (int)Roles.SystemAdmin));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenInvalidRole_WhenPostPersons_ThenBadRequest()
    {
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            "/api/persons", Command(UniqueEmail(), (int)Roles.User));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminCaller_WhenPostPersons_ThenForbidden()
    {
        // AF-06c: only a System Admin may use path b.
        Authorize(TestTokens.ForRole((int)Roles.ScopeAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            "/api/persons", Command(UniqueEmail(), (int)Roles.ScopeAdmin));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPostPersons_ThenUnauthorized()
    {
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            "/api/persons", Command(UniqueEmail(), (int)Roles.ScopeAdmin));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 4: Run to verify they fail, then pass**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~PersonControllerCreateAdminTests"`
Expected: PASS (all five). If the controller/DI were missing it would fail to compile/authorize; confirm green before committing.

- [ ] **Step 5: Commit**

```bash
git add src/Presentation tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerCreateAdminTests.cs
git commit -m "feat: expose UC-06 create admin endpoint (path b)"
```

---

## Task 4: Create User handler (path a)

**Files:**
- Create: `src/Application/ArturRios.IdentityManager.Command/Input/CreateUserCommand.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Input/Validation/CreateUserCommandValidator.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Handlers/CreateUserCommandHandler.cs`
- Test: `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateUserCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `CreatePersonCommandOutput`, `PersonMessages`, `IEmailVerificationService`.
- Produces:
  - `class CreateUserCommand : BaseCommand { Guid ScopeId; string Name; string Email; string Password; long ActingPersonId; int ActingRole; }`
  - `class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>`
  - `class CreateUserCommandHandler(IValidator<CreateUserCommand>, IAsyncReadOnlyRepository<Scope> scopeReader, IAsyncReadOnlyRepository<Person> personReader, IAsyncRepository<Person> personWriter, IEmailVerificationService) : ICommandHandlerAsync<CreateUserCommand, CreatePersonCommandOutput>`

- [ ] **Step 1: Create the command**

Create `Input/CreateUserCommand.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to create a <c>User</c> within a scope (UC-06 path a). <see cref="ScopeId" /> comes from
///     the route; <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller
///     from the authenticated caller (for the AF-06e ownership check) and are never bound from the body.
/// </summary>
public class CreateUserCommand : BaseCommand
{
    public Guid ScopeId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public long ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
```

- [ ] **Step 2: Create the validator**

Create `Input/Validation/CreateUserCommandValidator.cs`:

```csharp
using ArturRios.IdentityManager.Shared.Messages;
using FluentValidation;

namespace ArturRios.IdentityManager.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="CreateUserCommand" /> (UC-06 path a, AF-06d). Scope existence,
///     ownership, and email uniqueness are enforced by the handler.
/// </summary>
public class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty().WithMessage(PersonMessages.NameRequired)
            .MaximumLength(200).WithMessage(PersonMessages.NameTooLong);

        RuleFor(command => command.Email)
            .NotEmpty().WithMessage(PersonMessages.EmailRequired)
            .EmailAddress().WithMessage(PersonMessages.EmailInvalid);

        RuleFor(command => command.Password)
            .NotEmpty().WithMessage(PersonMessages.PasswordRequired)
            .MinimumLength(8).WithMessage(PersonMessages.PasswordTooShort);
    }
}
```

- [ ] **Step 3: Write the failing handler test**

Create `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateUserCommandHandlerTests.cs`:

```csharp
using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for CreateUserCommandHandler (UC-06 path a): main flow + AF-06b (scope missing/deleted),
// AF-06e (actor not owner / owner / SystemAdmin bypass), AF-06a (duplicate email in scope).
public class CreateUserCommandHandlerTests
{
    private static Mock<IValidator<CreateUserCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<CreateUserCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateUserCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private static async Task<(AsyncFakeRepository<Scope> scopes, Scope scope)> ScopeStoreAsync()
    {
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = "Acme", IsDeleted = false };
        await scopes.CreateAsync(scope);
        return (scopes, scope);
    }

    private static CreateUserCommand Command(Guid scopeId, int actingRole, long actingPersonId) => new()
    {
        ScopeId = scopeId,
        Name = "User",
        Email = $"user-{Guid.NewGuid():N}@test.local",
        Password = "Str0ngPass!",
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    [UnitFact]
    public async Task GivenSystemAdminAndUniqueEmail_WhenHandlingCreateUser_ThenUserWithMembershipIsCreated()
    {
        // Given a SystemAdmin actor (bypasses ownership) and an empty scope
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);
        var command = Command(scope.PublicId, (int)Roles.SystemAdmin, actingPersonId: 1);

        // When
        var output = await handler.HandleAsync(command);

        // Then — output
        Assert.True(output.Success);
        Assert.Equal(scope.PublicId, output.Data!.ScopeId);
        Assert.Equal((int)Roles.User, output.Data.Role);
        Assert.Contains(PersonMessages.PersonCreatedSuccessfully, output.Messages);

        // Then — a User with a SCOPE_USER row pointing at the scope's internal Id
        var stored = (await persons.GetAllAsync()).Data!.Single();
        Assert.Equal((long)Roles.User, stored.RoleId);
        Assert.NotNull(stored.ScopeMembership);
        Assert.Equal(scope.Id, stored.ScopeMembership!.ScopeId);
        email.Verify(e => e.IssueAndSendAsync(It.IsAny<Person>()), Times.Once);
    }

    [UnitFact]
    public async Task GivenOwnerScopeAdmin_WhenHandlingCreateUser_ThenUserIsCreated()
    {
        // Given a ScopeAdmin actor who owns the scope
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var actor = new Person
        {
            RoleId = (long)Roles.ScopeAdmin,
            ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id }]
        };
        await persons.CreateAsync(actor);
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, (int)Roles.ScopeAdmin, actor.Id));

        // Then
        Assert.True(output.Success);
        Assert.Equal((int)Roles.User, output.Data!.Role);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwner_WhenHandlingCreateUser_ThenReturnsNotScopeOwner()
    {
        // Given a ScopeAdmin actor with no ownership of the scope (AF-06e)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var actor = new Person { RoleId = (long)Roles.ScopeAdmin };
        await persons.CreateAsync(actor);
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, (int)Roles.ScopeAdmin, actor.Id));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
        email.Verify(e => e.IssueAndSendAsync(It.IsAny<Person>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingCreateUser_ThenReturnsScopeNotFound()
    {
        // Given an empty scope store (AF-06b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(Command(Guid.NewGuid(), (int)Roles.SystemAdmin, 1));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDuplicateEmailInScope_WhenHandlingCreateUser_ThenReturnsEmailAlreadyExists()
    {
        // Given a scope that already has a User with the target email (AF-06a)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var command = Command(scope.PublicId, (int)Roles.SystemAdmin, 1);
        await persons.CreateAsync(new Person
        {
            Email = command.Email,
            RoleId = (long)Roles.User,
            IsDeleted = false,
            ScopeMembership = new ScopeUser { ScopeId = scope.Id }
        });
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
    }
}
```

- [ ] **Step 4: Run to verify it fails**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~CreateUserCommandHandlerTests"`
Expected: FAIL — `CreateUserCommandHandler` does not exist (compile error).

- [ ] **Step 5: Create the handler**

Create `Handlers/CreateUserCommandHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="CreateUserCommand" /> (UC-06 path a): validates input, verifies the target
///     scope exists and is active (AF-06b), enforces scope ownership for a Scope Admin actor (AF-06e),
///     checks the email is unique among the scope's Users (AF-06a), then creates a <c>User</c> with a
///     <c>SCOPE_USER</c> row and issues a verification token. A System Admin actor bypasses the
///     ownership check.
/// </summary>
public class CreateUserCommandHandler(
    IValidator<CreateUserCommand> validator,
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IEmailVerificationService emailVerification)
    : ICommandHandlerAsync<CreateUserCommand, CreatePersonCommandOutput>
{
    public async Task<DataOutput<CreatePersonCommandOutput?>> HandleAsync(CreateUserCommand command)
    {
        var output = DataOutput<CreatePersonCommandOutput?>.New;

        // AF-06d: validate input shape.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-06b: the target scope must exist and not be logically deleted.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(PersonMessages.ScopeNotFound);
        }

        // AF-06e: a Scope Admin actor may only act on a scope they own; a System Admin bypasses.
        if (command.ActingRole != (int)Roles.SystemAdmin)
        {
            var actorOwnsScope = await personReader.Query().AnyAsync(person =>
                person.Id == command.ActingPersonId &&
                person.ScopeOwnerships.Any(ownership => ownership.ScopeId == scope.Id));

            if (!actorOwnsScope)
            {
                return output.WithError(PersonMessages.NotScopeOwner);
            }
        }

        // AF-06a: a User's email must be unique among the scope's Users.
        var emailTaken = await personReader.Query().AnyAsync(person =>
            !person.IsDeleted && person.Email == command.Email &&
            person.ScopeMembership != null && person.ScopeMembership.ScopeId == scope.Id);

        if (emailTaken)
        {
            return output.WithError(PersonMessages.EmailAlreadyExists);
        }

        // Create the User with its SCOPE_USER membership row.
        var passwordHash = Hash.EncodeWithRandomSalt(command.Password, out var salt);

        var newPerson = new Person
        {
            Name = command.Name,
            Email = command.Email,
            PasswordHash = passwordHash,
            Salt = salt,
            RoleId = (long)Roles.User,
            ScopeMembership = new ScopeUser { ScopeId = scope.Id }
        };

        var creation = await personWriter.CreateAsync(newPerson);

        if (!creation.Success)
        {
            return output.WithErrors(creation.Errors);
        }

        // FR-EV-01/02: issue and send the verification token.
        await emailVerification.IssueAndSendAsync(newPerson);

        return output
            .WithData(new CreatePersonCommandOutput
            {
                Id = newPerson.PublicId,
                Name = newPerson.Name,
                Email = newPerson.Email,
                Role = (int)Roles.User,
                EmailVerified = newPerson.EmailVerified,
                ScopeId = scope.PublicId,
                CreatedAt = newPerson.CreatedAt
            })
            .WithMessage(PersonMessages.PersonCreatedSuccessfully);
    }
}
```

- [ ] **Step 6: Run to verify the handler tests pass**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~CreateUserCommandHandlerTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Application/ArturRios.IdentityManager.Command tests/Application/ArturRios.IdentityManager.Command.Tests/CreateUserCommandHandlerTests.cs
git commit -m "feat: add UC-06 create user handler (path a)"
```

---

## Task 5: Create User endpoint (path a) + functional tests

**Files:**
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs`
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`
- Modify: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/Support/TestTokens.cs`
- Test: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerCreateUserTests.cs`

**Interfaces:**
- Consumes: `CreateUserCommand`, `CreateUserCommandHandler`, `CreateUserCommandValidator`, `AuthenticatedUser` (`HttpContext.Items["User"]`).
- Produces: `PersonController.CreateUser` (`POST /api/scopes/{scopeId}/persons`); `TestTokens.For(int id, int role)`.

- [ ] **Step 1: Register the path-a validator and handler in DI**

In `AddDependencies()` in `Startup.cs`, directly after the path-b registrations from Task 3, add:

```csharp
        Builder.Services.AddScoped<IValidator<CreateUserCommand>, CreateUserCommandValidator>();
        Builder.Services
            .AddScoped<ICommandHandlerAsync<CreateUserCommand, CreatePersonCommandOutput>, CreateUserCommandHandler>();
```

- [ ] **Step 2: Add the path-a action to the controller**

In `PersonController.cs`, add these `using`s:

```csharp
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.Util.WebApi.Security.Records;
```

Add this action inside the `PersonController` class (after `CreateAdmin`):

```csharp
    /// <summary>
    ///     Creates a <c>User</c> within a scope (UC-06 path a). A System Admin or an owner of the scope
    ///     may call it; the ownership check (AF-06e) is enforced by the handler from the acting user.
    /// </summary>
    [HttpPost("scopes/{scopeId:guid}/persons")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<CreatePersonCommandOutput?>>> CreateUser(
        Guid scopeId, [FromBody] CreateUserCommand command)
    {
        command.ScopeId = scopeId;

        var actor = (AuthenticatedUser)HttpContext.Items["User"]!;
        command.ActingPersonId = actor.Id;
        command.ActingRole = actor.Role;

        var result = await commandMediator
            .ExecuteCommandAsync<CreateUserCommand, CreatePersonCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }
```

- [ ] **Step 3: Add an id-specific token helper to TestTokens**

In `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/Support/TestTokens.cs`, add this method inside the `TestTokens` class and make `ForRole` delegate to it:

```csharp
    /// <summary>Builds a bearer token for a specific person id and role value (see <c>Roles</c>).</summary>
    public static string For(int id, int role)
    {
        var claims = new AuthenticatedUser(id, role).ToTokenClaims();

        var configuration = new JwtConfiguration(
            3600,
            Environment.GetEnvironmentVariable(IssuerVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(AudienceVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(SecretVariable) ?? string.Empty,
            claims);

        return new JwtHandler().CreateToken(configuration);
    }
```

Then replace the body of `ForRole` with:

```csharp
    public static string ForRole(int role) => For(1, role);
```

- [ ] **Step 4: Write the failing functional tests**

Create `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerCreateUserTests.cs`:

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.WebApi.Tests;

[Collection(nameof(FunctionalCollection))]
public class PersonControllerCreateUserTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static CreateUserCommand Command() => new()
    {
        Name = "User", Email = $"user-{Guid.NewGuid():N}@test.local", Password = "Str0ngPass!"
    };

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null)
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

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPostScopePersons_ThenUserIsCreated()
    {
        // Given
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var command = Command();

        // When
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", command);

        // Then — response
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);

        // Then — a User with a SCOPE_USER row and a verification token
        await using var context = db.CreateContext();
        var person = await context.Persons.AsNoTracking().FirstAsync(p => p.Email == command.Email);
        Assert.Equal((long)Roles.User, person.RoleId);
        Assert.True(await context.ScopeUsers.AnyAsync(su => su.PersonId == person.Id && su.ScopeId == scope.Id));
        Assert.True(await context.EmailVerificationTokens.AnyAsync(t => t.PersonId == person.Id));
    }

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenPostScopePersons_ThenUserIsCreated()
    {
        // Given a ScopeAdmin who owns the scope, authenticated with their own person id
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.For((int)owner.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", Command());

        // Then
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwner_WhenPostScopePersons_ThenForbidden()
    {
        // Given a ScopeAdmin who does NOT own the scope (AF-06e)
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync();
        Authorize(TestTokens.For((int)admin.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", Command());

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenMissingScope_WhenPostScopePersons_ThenNotFound()
    {
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{Guid.NewGuid()}/persons", Command());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDuplicateEmailInScope_WhenPostScopePersons_ThenConflict()
    {
        // Given a scope where the email is already taken by a User
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var command = Command();
        var first = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", command);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // When posting the same email again
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", command);

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPlainUserCaller_WhenPostScopePersons_ThenForbidden()
    {
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.User));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", Command());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPostScopePersons_ThenUnauthorized()
    {
        var scope = await SeedScopeAsync();

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", Command());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~PersonControllerCreateUserTests"`
Expected: PASS (all seven).

- [ ] **Step 6: Commit**

```bash
git add src/Presentation tests/Presentation
git commit -m "feat: expose UC-06 create user endpoint (path a)"
```

---

## Task 6: Create Scope Owner handler (path c)

**Files:**
- Create: `src/Application/ArturRios.IdentityManager.Command/Input/CreateScopeOwnerCommand.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Input/Validation/CreateScopeOwnerCommandValidator.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Handlers/CreateScopeOwnerCommandHandler.cs`
- Test: `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateScopeOwnerCommandHandlerTests.cs`

**Interfaces:**
- Produces:
  - `class CreateScopeOwnerCommand : BaseCommand { Guid ScopeId; string Name; string Email; string Password; long ActingPersonId; int ActingRole; }`
  - `class CreateScopeOwnerCommandValidator : AbstractValidator<CreateScopeOwnerCommand>`
  - `class CreateScopeOwnerCommandHandler(IValidator<CreateScopeOwnerCommand>, IAsyncReadOnlyRepository<Scope> scopeReader, IAsyncReadOnlyRepository<Person> personReader, IAsyncRepository<Person> personWriter, IEmailVerificationService) : ICommandHandlerAsync<CreateScopeOwnerCommand, CreatePersonCommandOutput>`

- [ ] **Step 1: Create the command**

Create `Input/CreateScopeOwnerCommand.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to create a brand-new <c>ScopeAdmin</c> person directly as a co-owner of a scope
///     (UC-06 path c, FR-SC-12). <see cref="ScopeId" /> comes from the route;
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller (for the AF-06e ownership check) and are never bound from the body.
/// </summary>
public class CreateScopeOwnerCommand : BaseCommand
{
    public Guid ScopeId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public long ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
```

- [ ] **Step 2: Create the validator**

Create `Input/Validation/CreateScopeOwnerCommandValidator.cs`:

```csharp
using ArturRios.IdentityManager.Shared.Messages;
using FluentValidation;

namespace ArturRios.IdentityManager.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="CreateScopeOwnerCommand" /> (UC-06 path c, AF-06d). Scope
///     existence, ownership, and email uniqueness are enforced by the handler.
/// </summary>
public class CreateScopeOwnerCommandValidator : AbstractValidator<CreateScopeOwnerCommand>
{
    public CreateScopeOwnerCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty().WithMessage(PersonMessages.NameRequired)
            .MaximumLength(200).WithMessage(PersonMessages.NameTooLong);

        RuleFor(command => command.Email)
            .NotEmpty().WithMessage(PersonMessages.EmailRequired)
            .EmailAddress().WithMessage(PersonMessages.EmailInvalid);

        RuleFor(command => command.Password)
            .NotEmpty().WithMessage(PersonMessages.PasswordRequired)
            .MinimumLength(8).WithMessage(PersonMessages.PasswordTooShort);
    }
}
```

- [ ] **Step 3: Write the failing handler test**

Create `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateScopeOwnerCommandHandlerTests.cs`:

```csharp
using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for CreateScopeOwnerCommandHandler (UC-06 path c): main flow + AF-06b (scope
// missing/deleted), AF-06e (actor not owner), AF-06a (duplicate admin email system-wide).
public class CreateScopeOwnerCommandHandlerTests
{
    private static Mock<IValidator<CreateScopeOwnerCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<CreateScopeOwnerCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateScopeOwnerCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private static async Task<(AsyncFakeRepository<Scope> scopes, Scope scope)> ScopeStoreAsync()
    {
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = "Acme", IsDeleted = false };
        await scopes.CreateAsync(scope);
        return (scopes, scope);
    }

    private static CreateScopeOwnerCommand Command(Guid scopeId, int actingRole, long actingPersonId) => new()
    {
        ScopeId = scopeId,
        Name = "Owner",
        Email = $"owner-{Guid.NewGuid():N}@test.local",
        Password = "Str0ngPass!",
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    [UnitFact]
    public async Task GivenSystemAdminAndUniqueEmail_WhenHandlingCreateScopeOwner_ThenScopeAdminWithOwnershipIsCreated()
    {
        // Given a SystemAdmin actor (bypasses ownership) and an empty scope
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateScopeOwnerCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, (int)Roles.SystemAdmin, 1));

        // Then — output
        Assert.True(output.Success);
        Assert.Equal(scope.PublicId, output.Data!.ScopeId);
        Assert.Equal((int)Roles.ScopeAdmin, output.Data.Role);
        Assert.Contains(PersonMessages.PersonCreatedSuccessfully, output.Messages);

        // Then — a ScopeAdmin with a SCOPE_OWNER row for the scope
        var stored = (await persons.GetAllAsync()).Data!.Single();
        Assert.Equal((long)Roles.ScopeAdmin, stored.RoleId);
        Assert.Equal(scope.Id, Assert.Single(stored.ScopeOwnerships).ScopeId);
        email.Verify(e => e.IssueAndSendAsync(It.IsAny<Person>()), Times.Once);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwner_WhenHandlingCreateScopeOwner_ThenReturnsNotScopeOwner()
    {
        // Given a ScopeAdmin actor with no ownership of the scope (AF-06e)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var actor = new Person { RoleId = (long)Roles.ScopeAdmin };
        await persons.CreateAsync(actor);
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateScopeOwnerCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, (int)Roles.ScopeAdmin, actor.Id));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingCreateScopeOwner_ThenReturnsScopeNotFound()
    {
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateScopeOwnerCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);

        var output = await handler.HandleAsync(Command(Guid.NewGuid(), (int)Roles.SystemAdmin, 1));

        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenExistingAdminEmail_WhenHandlingCreateScopeOwner_ThenReturnsEmailAlreadyExists()
    {
        // Given an existing ScopeAdmin with the same email system-wide (AF-06a)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var command = Command(scope.PublicId, (int)Roles.SystemAdmin, 1);
        await persons.CreateAsync(new Person
        {
            Email = command.Email, RoleId = (long)Roles.ScopeAdmin, IsDeleted = false
        });
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateScopeOwnerCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
    }
}
```

- [ ] **Step 4: Run to verify it fails**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~CreateScopeOwnerCommandHandlerTests"`
Expected: FAIL — `CreateScopeOwnerCommandHandler` does not exist (compile error).

- [ ] **Step 5: Create the handler**

Create `Handlers/CreateScopeOwnerCommandHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="CreateScopeOwnerCommand" /> (UC-06 path c, FR-SC-12): validates input,
///     verifies the target scope exists and is active (AF-06b), enforces scope ownership for a Scope
///     Admin actor (AF-06e), checks the email is unique among admin persons system-wide (AF-06a), then
///     creates a <c>ScopeAdmin</c> with a <c>SCOPE_OWNER</c> row making them a co-owner, and issues a
///     verification token. A System Admin actor bypasses the ownership check.
/// </summary>
public class CreateScopeOwnerCommandHandler(
    IValidator<CreateScopeOwnerCommand> validator,
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IEmailVerificationService emailVerification)
    : ICommandHandlerAsync<CreateScopeOwnerCommand, CreatePersonCommandOutput>
{
    public async Task<DataOutput<CreatePersonCommandOutput?>> HandleAsync(CreateScopeOwnerCommand command)
    {
        var output = DataOutput<CreatePersonCommandOutput?>.New;

        // AF-06d: validate input shape.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-06b: the target scope must exist and not be logically deleted.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(PersonMessages.ScopeNotFound);
        }

        // AF-06e: a Scope Admin actor may only act on a scope they own; a System Admin bypasses.
        if (command.ActingRole != (int)Roles.SystemAdmin)
        {
            var actorOwnsScope = await personReader.Query().AnyAsync(person =>
                person.Id == command.ActingPersonId &&
                person.ScopeOwnerships.Any(ownership => ownership.ScopeId == scope.Id));

            if (!actorOwnsScope)
            {
                return output.WithError(PersonMessages.NotScopeOwner);
            }
        }

        // AF-06a: admin emails are unique system-wide.
        var emailTaken = await personReader.Query().AnyAsync(person =>
            !person.IsDeleted && person.Email == command.Email &&
            (person.RoleId == (long)Roles.SystemAdmin || person.RoleId == (long)Roles.ScopeAdmin));

        if (emailTaken)
        {
            return output.WithError(PersonMessages.EmailAlreadyExists);
        }

        // Create the ScopeAdmin with a SCOPE_OWNER row linking them to the scope as a co-owner.
        var passwordHash = Hash.EncodeWithRandomSalt(command.Password, out var salt);

        var newPerson = new Person
        {
            Name = command.Name,
            Email = command.Email,
            PasswordHash = passwordHash,
            Salt = salt,
            RoleId = (long)Roles.ScopeAdmin,
            ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id }]
        };

        var creation = await personWriter.CreateAsync(newPerson);

        if (!creation.Success)
        {
            return output.WithErrors(creation.Errors);
        }

        // FR-EV-01/02: issue and send the verification token.
        await emailVerification.IssueAndSendAsync(newPerson);

        return output
            .WithData(new CreatePersonCommandOutput
            {
                Id = newPerson.PublicId,
                Name = newPerson.Name,
                Email = newPerson.Email,
                Role = (int)Roles.ScopeAdmin,
                EmailVerified = newPerson.EmailVerified,
                ScopeId = scope.PublicId,
                CreatedAt = newPerson.CreatedAt
            })
            .WithMessage(PersonMessages.PersonCreatedSuccessfully);
    }
}
```

- [ ] **Step 6: Run to verify the handler tests pass**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~CreateScopeOwnerCommandHandlerTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Application/ArturRios.IdentityManager.Command tests/Application/ArturRios.IdentityManager.Command.Tests/CreateScopeOwnerCommandHandlerTests.cs
git commit -m "feat: add UC-06 create scope owner handler (path c)"
```

---

## Task 7: Create Scope Owner endpoint (path c) + functional tests

**Files:**
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs`
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`
- Test: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerCreateScopeOwnerTests.cs`

**Interfaces:**
- Consumes: `CreateScopeOwnerCommand`, `CreateScopeOwnerCommandHandler`, `CreateScopeOwnerCommandValidator`, `AuthenticatedUser`, `TestTokens.For`.
- Produces: `PersonController.CreateScopeOwner` (`POST /api/scopes/{scopeId}/owners`).

- [ ] **Step 1: Register the path-c validator and handler in DI**

In `AddDependencies()` in `Startup.cs`, after the path-a registrations from Task 5, add:

```csharp
        Builder.Services.AddScoped<IValidator<CreateScopeOwnerCommand>, CreateScopeOwnerCommandValidator>();
        Builder.Services
            .AddScoped<ICommandHandlerAsync<CreateScopeOwnerCommand, CreatePersonCommandOutput>, CreateScopeOwnerCommandHandler>();
```

- [ ] **Step 2: Add the path-c action to the controller**

In `PersonController.cs`, add this action inside the class (after `CreateUser`):

```csharp
    /// <summary>
    ///     Creates a brand-new <c>ScopeAdmin</c> person directly as a co-owner of a scope (UC-06 path
    ///     c, FR-SC-12). A System Admin or an owner of the scope may call it; the ownership check
    ///     (AF-06e) is enforced by the handler from the acting user.
    /// </summary>
    [HttpPost("scopes/{scopeId:guid}/owners")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<CreatePersonCommandOutput?>>> CreateScopeOwner(
        Guid scopeId, [FromBody] CreateScopeOwnerCommand command)
    {
        command.ScopeId = scopeId;

        var actor = (AuthenticatedUser)HttpContext.Items["User"]!;
        command.ActingPersonId = actor.Id;
        command.ActingRole = actor.Role;

        var result = await commandMediator
            .ExecuteCommandAsync<CreateScopeOwnerCommand, CreatePersonCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }
```

- [ ] **Step 3: Write the failing functional tests**

Create `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerCreateScopeOwnerTests.cs`:

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.WebApi.Tests;

[Collection(nameof(FunctionalCollection))]
public class PersonControllerCreateScopeOwnerTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static CreateScopeOwnerCommand Command() => new()
    {
        Name = "Owner", Email = $"owner-{Guid.NewGuid():N}@test.local", Password = "Str0ngPass!"
    };

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null)
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

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenPostScopeOwners_ThenCoOwnerIsCreated()
    {
        // Given a ScopeAdmin who owns the scope
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.For((int)owner.Id, (int)Roles.ScopeAdmin));
        var command = Command();

        // When
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/owners", command);

        // Then — response
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal((int)Roles.ScopeAdmin, response.Body?.Data?.Role);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);

        // Then — a ScopeAdmin with a SCOPE_OWNER row and a verification token
        await using var context = db.CreateContext();
        var person = await context.Persons.AsNoTracking().FirstAsync(p => p.Email == command.Email);
        Assert.Equal((long)Roles.ScopeAdmin, person.RoleId);
        Assert.True(await context.ScopeOwners.AnyAsync(so => so.PersonId == person.Id && so.ScopeId == scope.Id));
        Assert.True(await context.EmailVerificationTokens.AnyAsync(t => t.PersonId == person.Id));
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPostScopeOwners_ThenCoOwnerIsCreated()
    {
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/owners", Command());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwner_WhenPostScopeOwners_ThenForbidden()
    {
        // AF-06e
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync();
        Authorize(TestTokens.For((int)admin.Id, (int)Roles.ScopeAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/owners", Command());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenMissingScope_WhenPostScopeOwners_ThenNotFound()
    {
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{Guid.NewGuid()}/owners", Command());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDuplicateAdminEmail_WhenPostScopeOwners_ThenConflict()
    {
        var scope = await SeedScopeAsync();
        var existing = await SeedScopeAdminAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var command = Command();
        command.Email = existing.Email;

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/owners", command);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPostScopeOwners_ThenUnauthorized()
    {
        var scope = await SeedScopeAsync();

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/owners", Command());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~PersonControllerCreateScopeOwnerTests"`
Expected: PASS (all six).

- [ ] **Step 5: Commit**

```bash
git add src/Presentation tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerCreateScopeOwnerTests.cs
git commit -m "feat: expose UC-06 create scope owner endpoint (path c)"
```

---

## Task 8: Full-suite verification

**Files:** none (verification only).

- [ ] **Step 1: Run the entire unit suite**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: PASS — all unit tests, including the four new UC-06 handler/service test classes and two validator test classes.

- [ ] **Step 2: Run the entire functional suite**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Functional"`
Expected: PASS — all functional tests, including the three new `PersonController*Tests` classes.

- [ ] **Step 3: Confirm no stray build warnings for the new files**

Run: `dotnet build src/ArturRios.IdentityManager.sln`
Expected: Build succeeded, 0 errors.

---

## Self-Review notes

- **Spec coverage:** path a → Tasks 4/5; path b → Tasks 2/3; path c → Tasks 6/7; email verification (FR-EV-01/02) → Task 1; AF-06a/06b/06d/06e → covered in each handler + functional class; AF-06c → `[RoleRequirement]` + functional test (Task 3). Routing, messages/map, DI, and no-migration constraint all mapped to tasks.
- **Type consistency:** `CreatePersonCommandOutput`, `IEmailVerificationService.IssueAndSendAsync(Person)`, `PersonMessages.*`, and handler constructor shapes are used identically across tasks. `TestTokens.For(int,int)` is defined in Task 5 before Task 7 consumes it.
- **Acting-user access:** `(AuthenticatedUser)HttpContext.Items["User"]!` — key/type verified against the shipped `AuthenticationMiddleware` (attaches to `Items["User"]`) and `AuthenticatedUser(int Id, int Role)`.
