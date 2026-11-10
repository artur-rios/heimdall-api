using System.Net.Sockets;
using System.Security.Authentication;

namespace ArturRios.Heimdall.WebApi.Monitoring;

/// <summary>
///     Turns a failed check into one of a fixed set of words, the same vocabulary as yggdrasil's status API.
///     The report is readable by every container on the internal networks, so no exception text ever reaches
///     it: a database driver's message can carry the host, the database and the user.
/// </summary>
public static class HealthReason
{
    private const string Timeout = "timeout";

    public static string From(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return Timeout;
        }

        // The whole chain, not only the outermost: Npgsql and EF Core each wrap the socket's error in an
        // exception of their own.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case TimeoutException:
                    return Timeout;
                case AuthenticationException:
                    return "tls error";
                case SocketException socket:
                    return FromSocket(socket);
            }
        }

        return exception is HttpRequestException request ? FromRequest(request) : "request failed";
    }

    private static string FromSocket(SocketException socket) => socket.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "connection refused",
        SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => "name not resolved",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => "host unreachable",
        SocketError.ConnectionReset => "connection reset",
        SocketError.TimedOut => Timeout,
        _ => "connection failed"
    };

    private static string FromRequest(HttpRequestException request) => request.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "name not resolved",
        HttpRequestError.ConnectionError => "connection failed",
        HttpRequestError.SecureConnectionError => "tls error",
        HttpRequestError.ResponseEnded => "connection closed",
        HttpRequestError.InvalidResponse => "invalid response",
        _ => "request failed"
    };
}
