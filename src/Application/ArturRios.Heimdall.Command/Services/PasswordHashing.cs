using ArturRios.Util.Hashing;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     The Argon2id cost every password and code derivation in the process uses: the library's
///     600 MB / 16-lane default (NFR-18), unless <see cref="InsecureTestVariable" /> asks for the
///     cheapest cost Argon2id accepts.
/// </summary>
/// <remarks>
///     <para>
///         The switch exists for the test suite. A derivation at the production cost is 600 MB, and
///         the suite derives thousands of times: the CI agent's test process grew past its memory
///         limit and was killed, and Command.Tests spent most of its time in Argon2id. Nothing a test
///         asserts depends on the cost, only on derivation and verification agreeing, so the suite
///         sets the variable in <c>tests/default.runsettings</c> and derives through
///         <see cref="Configuration" /> everywhere, fixtures included.
///     </para>
///     <para>
///         A switch rather than three numbers, and named for what it is, so that it cannot be half
///         tuned: every stored hash can only be verified with the cost it was made with, so a changed
///         cost locks every person out. Start-up refuses it in the environments Heimdall is
///         deployed in, Production and homologation's Staging (<see cref="EnsureAllowed" />).
///     </para>
///     <para>
///         Read once, when the type is first used: the decoy hash and every gate call must agree for
///         the life of the process.
///     </para>
/// </remarks>
public static class PasswordHashing
{
    /// <summary>The environment variable that, set to <c>true</c>, selects <see cref="Insecure" />.</summary>
    public const string InsecureTestVariable = "HEIMDALL_INSECURE_TEST_PASSWORD_HASHING";

    /// <summary>The cheapest cost Argon2id accepts: one lane, one pass, 8 KB. For tests only.</summary>
    public static HashConfiguration Insecure { get; } = new(1, 1, 8);

    /// <summary>The cost in effect for this process.</summary>
    public static HashConfiguration Configuration { get; } = FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>
    ///     <see cref="Insecure" /> when <paramref name="read" /> gives <c>true</c> (ignoring case and
    ///     surrounding space) for <see cref="InsecureTestVariable" />; the production cost for anything
    ///     else, unset included.
    /// </summary>
    public static HashConfiguration FromEnvironment(Func<string, string?> read) =>
        string.Equals(read(InsecureTestVariable)?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            ? Insecure
            : new HashConfiguration();

    /// <summary>
    ///     Refuses <paramref name="configuration" /> when it is <see cref="Insecure" /> in an environment
    ///     Heimdall is deployed in.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="isDeployed" /> and <paramref name="configuration" /> is <see cref="Insecure" />.
    /// </exception>
    public static void EnsureAllowed(bool isDeployed, HashConfiguration configuration)
    {
        if (isDeployed && ReferenceEquals(configuration, Insecure))
        {
            throw new InvalidOperationException(
                $"{InsecureTestVariable} is set in a deployed environment. Refusing to start: it makes " +
                "every password hash trivial to brute-force, and no hash stored with it verifies once it is removed.");
        }
    }
}
