using System.Threading.Channels;
using ArturRios.Heimdall.Command.Services;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     The in-process queue between UC-12's handler and <see cref="PasswordRecoveryDispatcher" />.
///     Bounded, never blocks the caller, and holds only what the caller sent: an address and an
///     optional scope.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a queue, and why in memory.</b> AF-12a's anti-enumeration promise needs the request
///         path to do the same work for every address. Two shapes were considered:
///     </para>
///     <list type="bullet">
///         <item>
///             A constant response-time floor. To hide a Mailgun round trip it would have to exceed
///             Mailgun's worst latency — the client's timeout, a hundred seconds — or leak exactly on
///             the slow sends, and it would hold every caller's connection open for that long, which
///             turns the endpoint into a cheap way to tie up the server.
///         </item>
///         <item>
///             A durable outbox in the database. It survives a restart, but it would keep every
///             address anybody typed — registered or not — at rest, which is personal data of people
///             who are not users, with no retention rule covering it. And nothing here is worth that:
///             a lost request is one the caller simply repeats, since UC-12 promises nothing more
///             than "if the address is registered, a link is on its way".
///         </item>
///     </list>
///     <para>
///         The queue holds no secret: the reset token is created by the dispatcher, after the
///         request has left. It lives as long as the process does, and a restart drops whatever was
///         waiting — at most a few seconds' worth, given the per-address rate limit in front of it.
///     </para>
///     <para>
///         <b>Bounded, and dropping when full.</b> <see cref="Capacity" /> caps the memory a burst can
///         take while Mailgun is slow or down. A request that does not fit is dropped and logged
///         rather than waited for, because waiting would put Mailgun's latency back on the request
///         path — the thing the queue exists to remove.
///     </para>
/// </remarks>
public sealed class PasswordRecoveryQueue(ILogger<PasswordRecoveryQueue> logger) : IPasswordRecoveryQueue
{
    /// <summary>Requests held at most while the dispatcher catches up.</summary>
    public const int Capacity = 1024;

    private readonly Channel<PasswordRecoveryRequest> _channel = Channel.CreateBounded<PasswordRecoveryRequest>(
        new BoundedChannelOptions(Capacity)
        {
            // Wait, not one of the Drop modes: TryWrite never waits in any mode, and Wait is the one
            // where a full queue makes TryWrite report false instead of silently discarding an item.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    /// <summary>The dispatcher's end of the queue.</summary>
    public ChannelReader<PasswordRecoveryRequest> Reader => _channel.Reader;

    public bool TryEnqueue(PasswordRecoveryRequest request)
    {
        if (_channel.Writer.TryWrite(request))
        {
            return true;
        }

        // No address in the log (NFR-22): which request was dropped does not matter, that one was.
        logger.LogWarning(
            "The password recovery queue is full ({Capacity}); a recovery request was dropped", Capacity);

        return false;
    }
}
