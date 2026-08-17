using ArturRios.Util.Hashing;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Bounds how many Argon2id derivations run at once, and refuses rather than queueing without
///     limit when that bound is reached (Threat Model TH-03).
/// </summary>
/// <remarks>
///     <para>
///         Argon2id is configured for 600 MB and 16 threads, which is deliberate: it is what makes an
///         offline attack on a stolen hash expensive (NFR-18). It also means concurrency, not CPU, is
///         what decides whether this API stays up. Ten derivations at once want six gigabytes of
///         working set, and until this existed nothing in the process knew that — the only thing
///         between a caller and unbounded memory demand was the per-IP rate limiter, which is per
///         instance and per address, so callers spread across addresses multiplied that budget rather
///         than sharing it.
///     </para>
///     <para>
///         An instance holding its own semaphore, with one <see cref="Shared" /> gate that every call
///         site uses. The bound is a process-wide property, so a single shared instance is what
///         expresses it — but making the class itself static put a mutable semaphore in global state,
///         and the suite found two faults in that within one run: a permit released into a semaphore
///         it had not been taken from, because reconfiguring replaced the instance under callers
///         already inside it; and unrelated handler tests refused, because another test class running
///         in parallel had set the bound to one. A gate's semaphore is now fixed at construction and
///         tests build their own.
///     </para>
///     <para>
///         The bound covers every derivation on a request path, not only login. A limit covering the
///         anonymous endpoints alone would leave an authenticated caller creating persons in a loop
///         to exhaust the same memory, and a bound with a way around it is not a bound.
///     </para>
///     <para>
///         What it deliberately does not cover: the decoy hash computed once at type initialisation,
///         and the master user the seeder writes at start-up. Neither is on a request path, both run
///         before the API serves anything, and gating start-up work against a limit meant for
///         concurrent requests would only risk a start-up that waits on itself.
///     </para>
/// </remarks>
public sealed class PasswordHashGate
{
    /// <summary>
    ///     Derivations permitted at once. Four is roughly 2.4 GB of Argon2id working set, which fits
    ///     a small container with room left for everything else the process does.
    /// </summary>
    /// <remarks>
    ///     Chosen to be survivable rather than fast, and below the ten a single address can already
    ///     demand through the rate limiter — so a burst from one caller queues instead of arriving at
    ///     once. That is the behaviour being bought, and it is not free: see <see cref="MaxWait" />.
    /// </remarks>
    public const int DefaultMaxConcurrent = 4;

    /// <summary>The default wait for a permit before the request is refused.</summary>
    /// <remarks>
    ///     Long enough to absorb a burst — a few derivations ahead of it, each a fraction of a second
    ///     — and short enough that a saturated process answers rather than holding connections open
    ///     until something else times out. Refusing is the point: an unbounded queue is the condition
    ///     this class exists to prevent, moved from memory into the thread pool.
    /// </remarks>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(10);

    /// <summary>The environment variable overriding <see cref="DefaultMaxConcurrent" />.</summary>
    public const string MaxConcurrentVariable = "HEIMDALL_AUTH_MAX_CONCURRENT_PASSWORD_HASHES";

    private readonly SemaphoreSlim _permits;

    public PasswordHashGate(int maxConcurrent, TimeSpan maxWait)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);

        MaxConcurrent = maxConcurrent;
        MaxWait = maxWait;
        _permits = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <summary>Derivations this gate permits at once.</summary>
    public int MaxConcurrent { get; }

    /// <summary>How long a caller waits for a permit before being refused.</summary>
    public TimeSpan MaxWait { get; }

    /// <summary>The gate every call site uses, and the one the process-wide bound belongs to.</summary>
    /// <remarks>
    ///     Settable so start-up can apply the configured bound, and so the functional suite can prove
    ///     a saturated gate answers 503 without waiting out the real ten seconds. Replaced rather than
    ///     reconfigured: a gate's semaphore never changes after construction, so a derivation already
    ///     running finishes against the gate it entered rather than releasing into a different one.
    /// </remarks>
    public static PasswordHashGate Shared { get; set; } = new(DefaultMaxConcurrent, DefaultMaxWait);

    /// <summary>
    ///     Reads <see cref="MaxConcurrentVariable" /> and replaces <see cref="Shared" />, falling back
    ///     to <see cref="DefaultMaxConcurrent" /> when it is unset, unparseable, or not positive.
    /// </summary>
    /// <remarks>
    ///     A bad value falls back rather than failing start-up. The variable tunes a safety margin;
    ///     refusing to boot over a typo in it would turn a conservative default into an outage.
    /// </remarks>
    public static void ConfigureSharedFromEnvironment() =>
        Shared = new PasswordHashGate(
            int.TryParse(Environment.GetEnvironmentVariable(MaxConcurrentVariable), out var configured)
            && configured > 0
                ? configured
                : DefaultMaxConcurrent,
            DefaultMaxWait);

    /// <summary>Verifies <paramref name="text" /> against a stored hash, under the bound.</summary>
    public Task<bool> TextMatchesAsync(string text, byte[] hash, byte[] salt) =>
        RunDerivationAsync(() => Hash.TextMatches(text, hash, salt));

    /// <summary>Derives a hash for <paramref name="text" /> under a fresh salt, under the bound.</summary>
    public async Task<(byte[] Hash, byte[] Salt)> EncodeWithRandomSaltAsync(string text)
    {
        byte[] salt = [];

        var hash = await RunDerivationAsync(() => Hash.EncodeWithRandomSalt(text, out salt));

        return (hash, salt);
    }

    /// <summary>
    ///     Runs <paramref name="work" /> once a permit is free, releasing it afterwards however
    ///     <paramref name="work" /> ends.
    /// </summary>
    /// <exception cref="PasswordHashGateSaturatedException">No permit became free in time.</exception>
    /// <remarks>
    ///     The primitive the two derivation helpers are written in terms of, and public because the
    ///     bound is about total concurrent Argon2id cost rather than about which helper incurs it: a
    ///     future caller with its own derivation should enter this gate rather than add a second one
    ///     beside it.
    /// </remarks>
    public async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        if (!await _permits.WaitAsync(MaxWait))
        {
            throw new PasswordHashGateSaturatedException();
        }

        try
        {
            return await work();
        }
        finally
        {
            // In a finally so a derivation that throws does not leak its permit. A leaked permit
            // closes the gate a little further each time, until the protection becomes the outage.
            _permits.Release();
        }
    }

    /// <inheritdoc cref="RunAsync{T}(Func{Task{T}})" />
    public async Task RunAsync(Func<Task> work) =>
        await RunAsync(async () =>
        {
            await work();

            return true;
        });

    /// <summary>Runs a synchronous derivation under the bound.</summary>
    /// <remarks>
    ///     Inline rather than on a worker thread. The derivation is CPU-bound and already
    ///     multi-threaded internally, and the permit count is what bounds how many run at once —
    ///     handing it to the thread pool as well would add a scheduling hop and bound nothing.
    /// </remarks>
    private Task<T> RunDerivationAsync<T>(Func<T> derivation) =>
        RunAsync(() => Task.FromResult(derivation()));
}

/// <summary>
///     Thrown when a password derivation could not start because the gate was already running
///     <see cref="PasswordHashGate.MaxConcurrent" /> of them and none finished in time.
/// </summary>
/// <remarks>
///     A load condition rather than a fault: nothing is wrong with the request, and the caller should
///     retry. <c>PasswordHashSaturationFilter</c> turns it into the canonical
///     <c>AuthenticationTemporarilyUnavailable</c> message and <c>503</c>.
/// </remarks>
public sealed class PasswordHashGateSaturatedException()
    : Exception("The password verification gate is saturated.");
