using Serilog;

namespace ArturRios.Heimdall.WebApi;

internal static class ConfigurationWarnings
{
    /// <summary>
    ///     Warns that a numeric setting was present but unusable and its default applied instead. Absent
    ///     is not a fallback, so <paramref name="rejectedValue" /> is <c>null</c> for it and nothing is
    ///     logged. None of these values is a secret, so the rejected text is logged as written.
    /// </summary>
    internal static void WarnOnFallback(string variable, string? rejectedValue, double defaultSeconds)
    {
        if (rejectedValue is null)
        {
            return;
        }

        Log.Warning(
            "Ignoring unusable {Variable}={Value}; using the default of {Default} seconds",
            variable, rejectedValue, defaultSeconds);
    }
}
