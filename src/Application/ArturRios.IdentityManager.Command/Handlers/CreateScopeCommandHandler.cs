using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Messages;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="CreateScopeCommand" /> (UC-01): validates the request, verifies the scope
///     name is unique and every owner is an existing, non-logically-deleted <c>ScopeAdmin</c>, then
///     persists the scope together with a <c>SCOPE_OWNER</c> row for each owner. All failures are
///     returned as errors on the <see cref="DataOutput{T}" /> rather than thrown, using the canonical
///     <see cref="ScopeMessages" /> so the response resolver can pick the matching status code.
/// </summary>
public class CreateScopeCommandHandler(
    IValidator<CreateScopeCommand> validator,
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<Role> roleReader,
    IAsyncRepository<Scope> scopeWriter)
    : ICommandHandlerAsync<CreateScopeCommand, CreateScopeCommandOutput>
{
    public async Task<DataOutput<CreateScopeCommandOutput?>> HandleAsync(CreateScopeCommand command)
    {
        var output = DataOutput<CreateScopeCommandOutput?>.New;

        // Step 2 (AF-01b): validate input fields.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        var ownerIds = command.OwnerIds.Distinct().ToList();

        // Step 3 (AF-01a): the scope name must be unique.
        var nameAlreadyExists = await scopeReader.Query().AnyAsync(x => x.Name == command.Name);

        if (nameAlreadyExists)
        {
            output.AddError(ScopeMessages.NameAlreadyExists);
        }

        // Step 4 (AF-01d): every owner must be an existing, non-logically-deleted ScopeAdmin.
        var scopeAdminRole = await roleReader.Query()
            .FirstOrDefaultAsync(x => x.Name == nameof(Roles.ScopeAdmin));

        if (scopeAdminRole is null)
        {
            output.AddError(ScopeMessages.ScopeAdminRoleNotConfigured);

            return output;
        }

        var owners = await personReader.Query()
            .Where(x => ownerIds.Contains(x.PublicId) && !x.IsDeleted && x.RoleId == scopeAdminRole.Id)
            .ToListAsync();

        if (owners.Count != ownerIds.Count)
        {
            output.AddError(ScopeMessages.OwnerNotValidScopeAdmin);
        }

        if (!output.Success)
        {
            return output;
        }

        // Step 5: create the scope with a SCOPE_OWNER row for each owner (inserted atomically).
        var scope = new Scope
        {
            Name = command.Name,
            Description = command.Description,
            Owners = [.. owners.Select(owner => new ScopeOwner { PersonId = owner.Id })]
        };

        var creation = await scopeWriter.CreateAsync(scope);

        if (!creation.Success)
        {
            return output.WithErrors(creation.Errors);
        }

        // Step 6: return the created scope.
        return output
            .WithData(new CreateScopeCommandOutput
            {
                Id = scope.PublicId,
                Name = scope.Name,
                Description = scope.Description,
                GoogleSignInEnabled = scope.GoogleSignInEnabled,
                OwnerIds = [.. owners.Select(owner => owner.PublicId)],
                CreatedAt = scope.CreatedAt
            })
            .WithMessage(ScopeMessages.ScopeCreatedSuccessfully);
    }
}
