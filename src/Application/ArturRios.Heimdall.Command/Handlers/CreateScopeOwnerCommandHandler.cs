using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

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
    IScopeOwnershipChecker scopeOwnership,
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
        if (!await scopeOwnership.ActorMayManageScopeAsync(command.ActingRole, command.ActingPersonId, scope.Id))
        {
            return output.WithError(PersonMessages.NotScopeOwner);
        }

        // AF-06a: admin emails are unique system-wide, compared case-insensitively (LOWER() in SQL).
        var emailTaken = await personReader.Query().AnyAsync(person =>
            !person.IsDeleted && person.Email.ToLower() == command.Email.ToLower() &&
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
