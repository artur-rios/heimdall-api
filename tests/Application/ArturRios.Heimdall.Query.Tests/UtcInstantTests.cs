using ArturRios.Heimdall.Query.Input;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for UtcInstant (UC-48 from/to). ASP.NET Core binds an instant with no offset as
// DateTimeKind.Unspecified, which Npgsql refuses for a timestamptz parameter; every instant in this
// API is UTC, so an unspecified one is read as UTC and a local one converted.
public class UtcInstantTests
{
    [UnitFact]
    public void GivenAUtcInstant_WhenNormalised_ThenItIsUnchanged()
    {
        // Given
        var value = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        // When
        var normalised = UtcInstant.Of(value);

        // Then
        Assert.Equal(value, normalised);
        Assert.Equal(DateTimeKind.Utc, normalised.Kind);
    }

    [UnitFact]
    public void GivenAnInstantWithoutAnOffset_WhenNormalised_ThenItIsReadAsUtc()
    {
        // Given what the binder produces from "2026-10-14T12:00:00"
        var value = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Unspecified);

        // When
        var normalised = UtcInstant.Of(value);

        // Then — the same reading, now marked UTC
        Assert.Equal(value.Ticks, normalised.Ticks);
        Assert.Equal(DateTimeKind.Utc, normalised.Kind);
    }

    [UnitFact]
    public void GivenALocalInstant_WhenNormalised_ThenItIsConvertedToUtc()
    {
        // Given
        var value = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Local);

        // When
        var normalised = UtcInstant.Of(value);

        // Then
        Assert.Equal(value.ToUniversalTime(), normalised);
        Assert.Equal(DateTimeKind.Utc, normalised.Kind);
    }
}
