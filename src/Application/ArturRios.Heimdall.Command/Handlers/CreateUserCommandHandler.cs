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
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IAsyncRepository<Person> personWriter,
    IScopeOwnershipChecker scopeOwnership,
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
        if (!await scopeOwnership.ActorMayManageScopeAsync(command.ActingRole, command.ActingPersonId, scope.Id))
        {
            return output.WithError(PersonMessages.NotScopeOwner);
        }

        // AF-06a: a User's email must be unique among the scope's Users, compared case-insensitively
        // (LOWER() in SQL).
        var email = command.Email.ToLower();

        var emailTaken = await personReader.Query().AnyAsync(person =>
            !person.IsDeleted && person.Email.ToLower() == email &&
            person.ScopeMembership != null && person.ScopeMembership.ScopeId == scope.Id);

        if (emailTaken)
        {
            return output.WithError(PersonMessages.EmailAlreadyExists);
        }

        // FR-GO-07: the scope's address space is shared with its Google Users, so the second half of
        // the rule is a second read — the same pair GoogleSignInCommandHandler makes before signing a
        // Google account up, in the other order. Without it the rule held in one direction only: a
        // Google sign-up was refused an address a User held, while a User could still be created on
        // an address a Google User held, leaving one scope with two identities for one address.
        var takenByGoogleUser = await googleUserReader.Query()
            .AnyAsync(googleUser => googleUser.ScopeId == scope.Id && googleUser.Email.ToLower() == email);

        if (takenByGoogleUser)
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
