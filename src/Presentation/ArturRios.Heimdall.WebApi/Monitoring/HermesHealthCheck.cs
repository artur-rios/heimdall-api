using ArturRios.Heimdall.Query.HealthChecks;
using ArturRios.Heimdall.WebApi.Email;

namespace ArturRios.Heimdall.WebApi.Monitoring;

/// <summary>
///     Whether Hermes answers on its public health address. Not required: without Hermes the API still
///     signs people in, and only the email that had to go out is at risk.
/// </summary>
/// <remarks>
///     A plain <c>GET</c> on the client of its own: the key Heimdall sends email with goes nowhere near this
///     call, because the address needs none and the answer is the same without it.
/// </remarks>
public sealed class HermesHealthCheck(EmailDeliveryOptions options, IHttpClientFactory clients) : IServiceHealthCheck
{
    public const string ClientName = "hermes-health";

    private const string HealthPath = "/healthcheck";

    public string ServiceName => "Hermes";

    public bool IsRequired => false;

    public async Task<ServiceHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (!options.HermesConfigured)
        {
            return new ServiceHealthResult(HealthStatuses.NotConfigured);
        }

        // Headers only, then dispose: the body is never read.
        using var response = await clients.CreateClient(ClientName).GetAsync(
            $"{options.HermesBaseUrl.TrimEnd('/')}{HealthPath}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        return response.IsSuccessStatusCode
            ? new ServiceHealthResult(HealthStatuses.Healthy)
            : new ServiceHealthResult(HealthStatuses.Unhealthy, $"HTTP {(int)response.StatusCode}");
    }
}
