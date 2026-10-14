using System.Globalization;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     The audience and lifetime of application tokens (UC-56, FR-AP-13), read once at start-up.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the audience is required and distinct.</b> Application tokens are signed with the
///         person tokens' key pair. A consumer that validates person tokens offline would take an
///         application token for a person if both carried one audience; a distinct audience is what
///         makes it refuse (Threat Model TH-35). So start-up refuses a missing audience, and one equal
///         to <c>HEIMDALL_AUTH_TOKEN_AUDIENCE</c>, rather than run without the separation.
///     </para>
///     <para>
///         <b>Lifetime.</b> Ten minutes by default (TH-34): short enough that a leaked token is soon
///         worthless, while UC-57's live re-check makes a revocation effective at once regardless. An
///         unusable value falls back to the default rather than stopping start-up, as
///         <c>HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS</c> does.
///     </para>
/// </remarks>
/// <param name="Audience">The <c>aud</c> every application token carries and UC-57 requires.</param>
/// <param name="Lifetime">How long an application token is valid after issue.</param>
public sealed record ApplicationTokenOptions(string Audience, TimeSpan Lifetime)
{
    /// <summary>The variable naming the application token audience. Required.</summary>
    public const string AudienceVariable = "HEIMDALL_APPLICATION_TOKEN_AUDIENCE";

    /// <summary>The variable carrying the lifetime in seconds. Optional.</summary>
    public const string ExpirationVariable = "HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS";

    private const string PersonAudienceVariable = "HEIMDALL_AUTH_TOKEN_AUDIENCE";

    private const double MaxLifetimeInSeconds = 86400;

    /// <summary>The lifetime used when none, or an unusable one, is configured.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Reads the options from the environment; throws when the audience is unusable.</summary>
    public static ApplicationTokenOptions FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(AudienceVariable),
        Environment.GetEnvironmentVariable(ExpirationVariable),
        Environment.GetEnvironmentVariable(PersonAudienceVariable));

    /// <summary>
    ///     Builds the options from raw values. Throws <see cref="InvalidOperationException" /> when
    ///     <paramref name="audience" /> is blank or equals <paramref name="personAudience" />.
    /// </summary>
    public static ApplicationTokenOptions Parse(string? audience, string? expirationInSeconds, string? personAudience)
    {
        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new InvalidOperationException(
                $"Environment variable '{AudienceVariable}' is unset. Application tokens need an " +
                $"audience of their own, different from '{PersonAudienceVariable}'.");
        }

        var trimmed = audience.Trim();

        if (string.Equals(trimmed, personAudience?.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Environment variable '{AudienceVariable}' equals '{PersonAudienceVariable}'. An " +
                "application token must carry an audience person tokens do not, or a consumer " +
                "validating person tokens would accept it as one.");
        }

        var lifetime = double.TryParse(
                           expirationInSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                       double.IsFinite(seconds) && seconds > 0 && seconds <= MaxLifetimeInSeconds
            ? TimeSpan.FromSeconds(seconds)
            : DefaultLifetime;

        return new ApplicationTokenOptions(trimmed, lifetime);
    }
}
