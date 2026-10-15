using System.Runtime.CompilerServices;
using ArturRios.Heimdall.Command.Services;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>
///     Installs a patient <see cref="PasswordHashGate" /> as <see cref="PasswordHashGate.Shared" /> for
///     the whole test assembly.
/// </summary>
/// <remarks>
///     Production Argon2id is 600 MB and 16 lanes, and about fourteen test classes derive through the
///     shared gate. Under CPU or memory pressure a derivation queued behind others outlasted the
///     production ten-second wait and the test failed on a refusal that says nothing about the code
///     under test. The permit count stays the production one, so memory demand is unchanged; only the
///     wait is longer. A module initializer rather than an xUnit fixture because it runs when the
///     assembly loads, before any test class, collection or fixture exists, and needs no attribute to
///     be wired in. Tests that build their own gate, such as the saturation tests, are unaffected.
/// </remarks>
internal static class CommandTestGate
{
    private static readonly TimeSpan PatientWait = TimeSpan.FromMinutes(2);

    /// <summary>The gate this initializer installed, which a test can compare <c>Shared</c> against.</summary>
    internal static PasswordHashGate Installed { get; } =
        new(PasswordHashGate.DefaultMaxConcurrent, PatientWait);

    [ModuleInitializer]
    internal static void Install() => PasswordHashGate.Shared = Installed;
}
