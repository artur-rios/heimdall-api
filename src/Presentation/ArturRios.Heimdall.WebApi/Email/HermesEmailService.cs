using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArturRios.Messaging.Email;
using ArturRios.Output;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Delivers Heimdall's email through Hermes, the internal email service, at its address on the edge network.
///     Hermes holds the Mailgun credentials and the sender: this sends only the key, the recipient, the subject and
///     the body.
/// </summary>
/// <remarks>
///     Keeps the contract of <see cref="MailgunEmailService" /> that every sender was written against: a refusal is
///     reported in the returned <see cref="ProcessOutput" />, and failing to reach Hermes is thrown. One attempt, as
///     Hermes does not want retries. The error names the status only, so nothing from the message can reach a log.
/// </remarks>
public class HermesEmailService(HttpClient httpClient, EmailDeliveryOptions options) : IEmailService
{
    private const string EmailsPath = "/api/emails";

    public Task<ProcessOutput> SendEmailAsync(string to, string subject, string body) =>
        SendEmailAsync(to, subject, body, CancellationToken.None);

    public async Task<ProcessOutput> SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.HermesBaseUrl.TrimEnd('/')}{EmailsPath}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", options.HermesApiKey) },
            Content = JsonContent.Create(new { to, subject, body })
        };

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var output = new ProcessOutput();

        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            output.AddError($"Hermes did not accept the message. Status: {(int)response.StatusCode}");
        }

        return output;
    }
}
