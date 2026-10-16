using System.Security.Cryptography;
using System.Text;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     How many registration-attempt notices (UC-47 AF-47b) one address has been sent in its current
///     window (FR-SR-14): at most <see cref="MaxNotices" /> a day, whichever scopes the attempts named.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why it exists.</b> Registering with an address that is already taken mails its holder a
///         notice. The response is the same either way, so the caller cannot tell the notice went out
///         — but the holder can, once per request. The per-IP rate limit bounds one caller, not one
///         recipient: an attacker with many source addresses could otherwise fill an inbox with them.
///     </para>
///     <para>
///         <b>What it holds.</b> The address as a SHA-256 digest of its trimmed, lower-cased form —
///         never the address itself — the notices sent in the window, and when the window ends. The
///         first notice after a window has ended opens a new one. A row is purged with the
///         single-use tokens once its window has ended and the grace period passed (NFR-19), so no
///         row outlives its use by more than that.
///     </para>
///     <para>
///         The digest is pseudonymisation, not anonymisation: whoever already suspects an address can
///         hash it and look for it. It is personal data for that reason, and recorded as such in the
///         Data Protection Document.
///     </para>
/// </remarks>
public class AttemptNoticeBudget : Entity<long>
{
    /// <summary>The notices one address may be sent in a window.</summary>
    public const int MaxNotices = 3;

    /// <summary>How long a window lasts, from the first notice sent in it.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);

    /// <summary>
    ///     SHA-256 of the trimmed, lower-cased address, as lowercase hex (<see cref="AddressHashOf" />).
    ///     Unique: one row per address.
    /// </summary>
    public string AddressHash { get; set; } = string.Empty;

    /// <summary>Notices sent to the address since its current window opened.</summary>
    public int NoticesSent { get; set; }

    /// <summary>When the current window ends; the next notice after it opens a new one.</summary>
    public DateTime WindowEndsAt { get; set; }

    /// <summary>
    ///     The digest an address is counted under. Case and surrounding spaces do not make another
    ///     address, as they do not for the scope uniqueness rule (FR-PE-09).
    /// </summary>
    public static string AddressHashOf(string address) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address.Trim().ToLowerInvariant())));
}
