using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DeployKit.DeployServer;

public class JobQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    // Tracks cancelled job IDs so they can be skipped on dequeue
    private readonly ConcurrentDictionary<string, bool> _cancelled = new();

    public async ValueTask EnqueueAsync(string jobId, CancellationToken ct = default)
    {
        await _channel.Writer.WriteAsync(jobId, ct);
    }

    /// <summary>
    /// Tries to read the next non-cancelled job ID from the queue.
    /// Returns null immediately if the queue is empty (non-blocking, matches original Redis RPOP behavior).
    /// </summary>
    public string? TryDequeue()
    {
        while (_channel.Reader.TryRead(out var jobId))
        {
            if (_cancelled.TryRemove(jobId, out _))
                continue; // skip cancelled
            return jobId;
        }
        return null;
    }

    public void MarkCancelled(string jobId) => _cancelled[jobId] = true;

    /// <summary>
    /// Re-enqueues pending jobs from the DB on startup (crash recovery).
    /// </summary>
    public async Task RecoverAsync(IEnumerable<string> pendingJobIds)
    {
        foreach (var id in pendingJobIds)
            await _channel.Writer.WriteAsync(id);
    }
}
