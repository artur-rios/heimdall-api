using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for IdentityAnonymiser (NFR-20). The guarantee under test is the one the whole erasure
// obligation rests on: after the operation, no value on the record identifies a natural person, and
// nothing structural has moved.
//
// The tests assert against the *original* values rather than against the placeholders, so a change
// that stopped overwriting a column fails here even if the placeholder format changes.
public class IdentityAnonymiserTests
{
    private const string RealName = "Ada Lovelace";
    private const string RealEmail = "ada@analytical.engine";

    private static Person LivePerson() => new()
    {
        Id = 42,
        PublicId = Guid.NewGuid(),
        Name = RealName,
        Email = RealEmail,
        PasswordHash = [1, 2, 3, 4],
        Salt = [5, 6, 7, 8],
        IsDeleted = true,
        EmailVerified = true,
        FailedLoginAttempts = 3,
        LockedOutUntil = DateTime.UtcNow.AddMinutes(10),
        RoleId = (long)Roles.User,
        ScopeId = 7,
        DeletedAt = DateTime.UtcNow.AddDays(-100),
        DeletionKind = (int)DeletionKinds.Administrative
    };

    private static GoogleUser LiveGoogleUser() => new()
    {
        Id = 43,
        PublicId = Guid.NewGuid(),
        GoogleId = "108124442542040415000",
        Name = RealName,
        Email = RealEmail,
        EmailVerified = true,
        ProfilePictureUrl = "https://lh3.googleusercontent.com/a/photo-of-a-person",
        IsDeleted = true,
        ScopeId = 7,
        DeletedAt = DateTime.UtcNow.AddDays(-100),
        DeletionKind = (int)DeletionKinds.Administrative
    };

    [UnitFact]
    public void GivenADeletedPerson_WhenAnonymised_ThenNoIdentifyingValueRemains()
    {
        var person = LivePerson();
        var at = DateTime.UtcNow;

        IdentityAnonymiser.Anonymise(person, at);

        // The name and the address are gone, and gone specifically — not merely different
        Assert.NotEqual(RealName, person.Name);
        Assert.NotEqual(RealEmail, person.Email);
        Assert.Equal(IdentityAnonymiser.AnonymisedName, person.Name);
        Assert.EndsWith($"@{IdentityAnonymiser.AnonymisedEmailDomain}", person.Email);

        // The credential material is gone: it is what an offline attack works on, and it protects
        // an account nobody can reach
        Assert.Empty(person.PasswordHash);
        Assert.Empty(person.Salt);

        // Behavioural data describes a person as surely as their name does
        Assert.Equal(0, person.FailedLoginAttempts);
        Assert.Null(person.LockedOutUntil);

        // An anonymised address is not a verified one
        Assert.False(person.EmailVerified);

        Assert.Equal(at, person.AnonymisedAt);
        Assert.Equal(at, person.UpdatedAt);
    }

    [UnitFact]
    public void GivenADeletedPerson_WhenAnonymised_ThenEveryStructuralValueSurvives()
    {
        var person = LivePerson();
        var publicId = person.PublicId;

        IdentityAnonymiser.Anonymise(person, DateTime.UtcNow);

        // NFR-07: the row keeps every key anything else points at or joins on. Removing the row is
        // what breaks that, and is why anonymisation overwrites in place instead.
        Assert.Equal(42, person.Id);
        Assert.Equal(publicId, person.PublicId);
        Assert.Equal((long)Roles.User, person.RoleId);
        Assert.Equal(7, person.ScopeId);

        // Still deleted: anonymisation is the terminal state of a deletion, not a resurrection
        Assert.True(person.IsDeleted);
        Assert.NotNull(person.DeletedAt);
    }

    [UnitFact]
    public void GivenADeletedGoogleUser_WhenAnonymised_ThenNoIdentifyingValueRemains()
    {
        var googleUser = LiveGoogleUser();
        var at = DateTime.UtcNow;
        var googleId = googleUser.GoogleId;

        IdentityAnonymiser.Anonymise(googleUser, at);

        Assert.NotEqual(RealName, googleUser.Name);
        Assert.NotEqual(RealEmail, googleUser.Email);

        // Google's 'sub' is the strongest identifier here: stable across Google's whole estate, so
        // anyone holding it could re-identify the person from outside this system entirely
        Assert.NotEqual(googleId, googleUser.GoogleId);

        // A profile picture is a photograph of a person
        Assert.Null(googleUser.ProfilePictureUrl);

        Assert.False(googleUser.EmailVerified);
        Assert.Equal(at, googleUser.AnonymisedAt);
    }

    [UnitFact]
    public void GivenADeletedGoogleUser_WhenAnonymised_ThenStructuralValuesSurvive()
    {
        var googleUser = LiveGoogleUser();
        var publicId = googleUser.PublicId;

        IdentityAnonymiser.Anonymise(googleUser, DateTime.UtcNow);

        Assert.Equal(publicId, googleUser.PublicId);
        Assert.Equal(7, googleUser.ScopeId);
        Assert.True(googleUser.IsDeleted);
    }

    [UnitFact]
    public void GivenTwoIdentitiesInOneScope_WhenAnonymised_ThenTheirPlaceholdersDiffer()
    {
        // GOOGLE_USER carries unconditional unique indexes on (scope_id, google_id) and
        // (scope_id, LOWER(email)). A constant placeholder would collide the moment a second Google
        // User in the same scope was anonymised, turning an erasure into a failed write.
        var first = LiveGoogleUser();
        var second = LiveGoogleUser();

        IdentityAnonymiser.Anonymise(first, DateTime.UtcNow);
        IdentityAnonymiser.Anonymise(second, DateTime.UtcNow);

        Assert.NotEqual(first.Email, second.Email);
        Assert.NotEqual(first.GoogleId, second.GoogleId);
    }

    [UnitFact]
    public void GivenAnAnonymisedIdentity_WhenItsAddressIsRead_ThenItCanNeverBeDelivered()
    {
        // RFC 2606 reserves .invalid and guarantees it never resolves. An erased person must not be
        // reachable by an email this system sends.
        var person = LivePerson();

        IdentityAnonymiser.Anonymise(person, DateTime.UtcNow);

        Assert.EndsWith(".invalid", person.Email);
    }
}
