namespace ArkServerManager.Core;

public sealed record IdleManagerShutdownResult(bool Accepted, string Message);

/// <summary>Prevents shutdown racing another client request, including requests waiting for lifecycle ownership.</summary>
public sealed class ManagerRequestDrain
{
    private readonly object sync = new();
    private int active;
    private bool draining;

    public IDisposable Enter()
    {
        lock (sync)
        {
            if (draining) throw new InvalidOperationException("The manager is closing for an app update.");
            active++;
            return new Lease(this);
        }
    }

    public bool TryBegin(BackgroundOperations operations)
    {
        lock (sync)
        {
            if (draining || active != 1 || !operations.TryQuiesce()) return false;
            draining = true;
            return true;
        }
    }

    private sealed class Lease(ManagerRequestDrain owner) : IDisposable
    {
        private ManagerRequestDrain? current = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref current, null);
            if (value is not null) lock (value.sync) value.active--;
        }
    }
}
