using System.Collections.Concurrent;

namespace TaskHive.Core.Realtime;

public enum BoardChangeKind
{
    TaskCreated,
    TaskUpdated,
    TaskMoved,
    TaskDeleted
}

public sealed record BoardChange(Guid ProjectId, BoardChangeKind Kind, Guid TaskId, string TaskKey, string ActorId, string ActorName);

/// <summary>
/// Publishes board changes to everyone currently viewing the same project.
/// </summary>
public interface IBoardNotifier
{
    IDisposable Subscribe(Guid projectId, Func<BoardChange, Task> handler);

    Task PublishAsync(BoardChange change);
}

/// <summary>
/// In-process implementation. Each Blazor Server circuit subscribes while its board is open and the
/// update reaches the browser over that circuit's SignalR connection. To run several app instances
/// behind a load balancer, replace this with a Redis pub/sub or SignalR backplane implementation.
/// </summary>
public sealed class InMemoryBoardNotifier : IBoardNotifier
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Func<BoardChange, Task>>> _subscribers = new();

    public IDisposable Subscribe(Guid projectId, Func<BoardChange, Task> handler)
    {
        var id = Guid.NewGuid();
        _subscribers.GetOrAdd(projectId, _ => new()).TryAdd(id, handler);
        return new Subscription(() =>
        {
            if (_subscribers.TryGetValue(projectId, out var handlers))
            {
                handlers.TryRemove(id, out _);
            }
        });
    }

    public async Task PublishAsync(BoardChange change)
    {
        if (!_subscribers.TryGetValue(change.ProjectId, out var handlers))
        {
            return;
        }

        // One slow or broken circuit must not block the others.
        await Task.WhenAll(handlers.Values.Select(async handler =>
        {
            try
            {
                await handler(change);
            }
            catch
            {
                // A disconnected circuit throws; it unsubscribes when it is disposed.
            }
        }));
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
