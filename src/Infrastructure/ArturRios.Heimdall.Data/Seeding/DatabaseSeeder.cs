using System.ComponentModel;
using System.Reflection;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Util.Hashing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Data.Seeding;

/// <summary>
///     Brings a migrated database to the state the application assumes: every <see cref="Roles" />
///     member present as a row, and at least one system administrator to sign in as. Idempotent, so
///     it runs on every startup. It never applies migrations — that is <c>scripts/migrations.py</c>'s
///     job — and refuses to seed a schema that is behind.
/// </summary>
public class DatabaseSeeder(
    AppDbContext context,
    MasterUserOptions masterUser,
    ILogger<DatabaseSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaIsUpToDateAsync(cancellationToken);
        await EnsureRolesAsync(cancellationToken);
        await EnsureSystemAdminAsync(cancellationToken);
    }

    private async Task EnsureSchemaIsUpToDateAsync(CancellationToken cancellationToken)
    {
        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        if (pending.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", pending);

        logger.LogCritical("Database is behind by {Count} migration(s): {Migrations}", pending.Count, names);

        throw new InvalidOperationException(
            $"The database is missing {pending.Count} migration(s): {names}. Apply them with " +
            "scripts/migrations.py before starting the API.");
    }

    private async Task EnsureRolesAsync(CancellationToken cancellationToken)
    {
        var stored = await context.Roles.ToDictionaryAsync(x => x.Id, cancellationToken);

        foreach (var role in Enum.GetValues<Roles>())
        {
            var id = (long)role;
            var name = role.ToString();
            var description = DescriptionOf(role);

            if (!stored.TryGetValue(id, out var existing))
            {
                context.Roles.Add(new Role { Id = id, Name = name, Description = description });

                logger.LogInformation("Seeding role {RoleName} with id {RoleId}", name, id);

                continue;
            }

            if (existing.Name == name && existing.Description == description)
            {
                continue;
            }

            existing.Name = name;
            existing.Description = description;

            logger.LogInformation("Realigning role {RoleId} with the Roles enum", id);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureSystemAdminAsync(CancellationToken cancellationToken)
    {
        const long systemAdminRoleId = (long)Roles.SystemAdmin;

        var exists = await context.Persons
            .AnyAsync(x => x.RoleId == systemAdminRoleId && !x.IsDeleted, cancellationToken);

        if (exists)
        {
            return;
        }

        if (!masterUser.IsComplete)
        {
            throw new InvalidOperationException(
                "The database has no system administrator and the master user is not configured. Set " +
                $"{MasterUserOptions.NameVariable}, {MasterUserOptions.EmailVariable} and " +
                $"{MasterUserOptions.PasswordVariable} before starting the API.");
        }

        var passwordHash = Hash.EncodeWithRandomSalt(masterUser.Password, out var salt);

        context.Persons.Add(new Person
        {
            Name = masterUser.Name,
            Email = masterUser.Email,
            PasswordHash = passwordHash,
            Salt = salt,
            EmailVerified = true,
            RoleId = systemAdminRoleId
        });

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded the master system administrator {Email}", masterUser.Email);
    }

    private static string DescriptionOf(Roles role) =>
        typeof(Roles).GetField(role.ToString())?.GetCustomAttribute<DescriptionAttribute>()?.Description
        ?? role.ToString();
}
