using System.Threading.Channels;
using ArturRios.Heimdall.Command.Services;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     The in-process queue between UC-47's handler and <see cref="RegistrationDispatcher" />. Bounded,
///     never blocks the caller. Shaped exactly like <see cref="PasswordRecoveryQueue" />, whose remarks
///     explain why it is in memory rather than a response-time floor or a database outbox.
/// </summary>
/// <remarks>
///     Unlike the recovery queue it holds a secret — a password hash and salt — for the seconds a
///     request waits. It is never written anywhere, and a restart drops it with the request, which the
///     caller simply repeats.
/// </remarks>
public sealed class RegistrationQueue(ILogger<RegistrationQueue> logger) : IRegistrationQueue
{
    /// <summary>Requests held at most while the dispatcher catches up.</summary>
    public const int Capacity = 1024;

    private readonly Channel<RegistrationRequest> _channel = Channel.CreateBounded<RegistrationRequest>(
        new BoundedChannelOptions(Capacity)
        {
            // Wait, not one of the Drop modes: see PasswordRecoveryQueue.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    /// <summary>The dispatcher's end of the queue.</summary>
    public ChannelReader<RegistrationRequest> Reader => _channel.Reader;

    public bool TryEnqueue(RegistrationRequest request)
    {
        if (_channel.Writer.TryWrite(request))
        {
            return true;
        }

        // No address in the log (NFR-22).
        logger.LogWarning(
            "The registration queue is full ({Capacity}); a registration request was dropped", Capacity);

        return false;
    }
}
