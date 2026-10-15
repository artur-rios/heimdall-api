using System.ComponentModel;

namespace ArturRios.Heimdall.Domain.Enums;

/// <summary>
///     Which table an <c>ErasedSubject</c>'s <c>PublicId</c> was taken from before its row was
///     hard-deleted.
/// </summary>
/// <remarks>
///     Nothing decides on this value: the retention pass treats both kinds alike. It is kept so an
///     operator reading the table can tell a removed person from a removed Google User without the
///     row that would once have said so.
/// </remarks>
public enum ErasedSubjectKinds
{
    /// <summary>A <c>PERSON</c> row, removed by UC-10 or by UC-05 as a member of its scope.</summary>
    [Description("A hard-deleted person")]
    Person = 1,

    /// <summary>A <c>GOOGLE_USER</c> row, removed by UC-29 or by UC-05 as a member of its scope.</summary>
    [Description("A hard-deleted Google User")]
    GoogleUser = 2
}
