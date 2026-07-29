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
