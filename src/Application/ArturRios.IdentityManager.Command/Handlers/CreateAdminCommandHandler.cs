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
