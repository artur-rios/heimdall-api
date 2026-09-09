using System.Reflection;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     GDPR Art. 30(1) and LGPD Art. 37 require a record of what personal data is processed; Art.
///     5(1)(c) and Art. 25 require each field to be necessary for a stated purpose. §5.1 of the Data
///     Protection Document is that record, and this makes it an enforced invariant rather than a
///     document that quietly falls behind the schema.
/// </summary>
/// <remarks>
///     A record of processing nobody notices going stale is worse than none, because it is relied
///     on — by the controller answering a regulator, and by anybody reading it to find out what the
///     system holds. Reflecting over the entities means a new column arrives with a stated purpose
///     or fails the build.
/// </remarks>
public class PersonalDataPurposeTests
{
    /// <summary>
    ///     The entities holding data about an identifiable person. `Application` is deliberately
    ///     absent: it is a non-human identity, and its `OwnerId` names a person but the row
    ///     describes the system.
    /// </summary>
    private static readonly Type[] PersonalDataEntities = [typeof(Person), typeof(GoogleUser), typeof(AuditLog)];

    /// <summary>
    ///     Navigation properties and the internal key. The record covers what is stored about a
    ///     person, and neither a collection of related rows nor the `bigint` that never leaves the
    ///     data layer (NFR-15) is that.
    /// </summary>
    private static bool IsStoredValue(PropertyInfo property)
    {
        if (property.Name == nameof(ArturRios.Data.Relational.Core.Entities.Entity.Id))
        {
            return false;
        }

        var type = property.PropertyType;

        if (type == typeof(string) || type.IsValueType || type == typeof(byte[]))
        {
            return true;
        }

        // Anything else is a navigation to another entity or a collection of them.
        return false;
    }

    private static string ReadRecord()
    {
        var root = Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                "..", "..", "..", "..", "..", "..", "docs", "requirements",
                "Data Protection Document.md"));

        Assert.True(File.Exists(root), $"The record of processing was not found at {root}");

        return File.ReadAllText(root);
    }

    [UnitFact]
    public void GivenTheEntities_WhenTheRecordIsRead_ThenEveryStoredValueHasAStatedPurpose()
    {
        var record = ReadRecord();

        var missing = PersonalDataEntities
            .SelectMany(entity => entity
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(IsStoredValue)
                .Select(property => (Entity: entity.Name, Property: property.Name)))
            .Where(column => !record.Contains($"`{column.Property}`"))
            .Select(column => $"{column.Entity}.{column.Property}")
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These columns hold personal data and are not listed in §5.1 of the Data Protection " +
            $"Document. Add each with the purpose that justifies holding it: {string.Join(", ", missing)}");
    }
}
