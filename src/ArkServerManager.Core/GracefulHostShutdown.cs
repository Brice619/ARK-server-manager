namespace ArkServerManager.Core;

public sealed record GracefulHostShutdownResult(bool Stopped, bool TimedOut, bool NoticeRecorded);

public static class GracefulHostShutdown
{
    public static async Task<GracefulHostShutdownResult> RunAsync(Func<CancellationToken, Task<LifecycleOperationResult>> stop,
        Func<ActivitySeverity, string, Task> report, TimeSpan limit)
    {
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromMinutes(5)) throw new ArgumentException("Choose a bounded shutdown deadline.");
        using var deadline = new CancellationTokenSource(limit);
        var stopped = false; var timedOut = false;
        var message = "Owned-server shutdown could not be completed. Review server activity.";
        try
        {
            var result = await stop(deadline.Token).WaitAsync(deadline.Token);
            stopped = result.Success && result.Members.All(member => member.State == ServerState.Stopped);
            message = stopped ? "Owned servers completed graceful manager shutdown."
                : "Some servers did not confirm shutdown. Their state requires review.";
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { timedOut = true; message = "The graceful manager shutdown deadline expired. Server shutdown was not confirmed."; }
        catch (Exception) { }
        var recorded = false;
        try
        {
            await report(stopped ? ActivitySeverity.Information : ActivitySeverity.Error, message).WaitAsync(TimeSpan.FromSeconds(5));
            recorded = true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException) { }
        return new(stopped, timedOut, recorded);
    }
}
