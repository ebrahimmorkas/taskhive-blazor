using System.Collections.Concurrent;

namespace TaskHive.Core.Realtime;

public sealed record PresentUser(string UserId, string DisplayName);

/// <summary>
/// Tracks who is looking at which board. A user with several tabs open counts once and stays
/// present until their last tab leaves.
/// </summary>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, PresentUser>> _viewers = new();

    public event Func<Guid, Task>? PresenceChanged;

    public async Task<IDisposable> JoinAsync(Guid projectId, PresentUser user)
    {
        var sessionId = Guid.NewGuid();
        _viewers.GetOrAdd(projectId, _ => new()).TryAdd(sessionId, user);
        await NotifyAsync(projectId);

        return new Session(async () =>
        {
            if (_viewers.TryGetValue(projectId, out var sessions) && sessions.TryRemove(sessionId, out _))
            {
                await NotifyAsync(projectId);
            }
        });
    }

    public IReadOnlyList<PresentUser> GetViewers(Guid projectId) =>
        _viewers.TryGetValue(projectId, out var sessions)
            ? sessions.Values.DistinctBy(u => u.UserId).OrderBy(u => u.DisplayName).ToList()
            : [];

    private async Task NotifyAsync(Guid projectId)
    {
        if (PresenceChanged is { } handler)
        {
            foreach (var subscriber in handler.GetInvocationList().Cast<Func<Guid, Task>>())
            {
                try
                {
                    await subscriber(projectId);
                }
                catch
                {
                    // Ignore disconnected circuits.
                }
            }
        }
    }

    private sealed class Session(Func<Task> leave) : IDisposable
    {
        private Func<Task>? _leave = leave;

        public void Dispose() => _ = Interlocked.Exchange(ref _leave, null)?.Invoke();
    }
}
