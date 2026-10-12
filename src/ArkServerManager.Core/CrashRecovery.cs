namespace ArkServerManager.Core;

/// <summary>A runtime crash allows two startup attempts, independent of healthy peers.</summary>
public sealed class CrashRecovery
{
    public int FailedStartupAttempts { get; private set; }
    public bool Recovering { get; private set; }
    public bool MayAttemptStartup => Recovering && FailedStartupAttempts < 2;

    public void RuntimeCrashed()
    {
        FailedStartupAttempts = 0;
        Recovering = true;
    }

    public void StartupFailed()
    {
        if (!MayAttemptStartup) throw new InvalidOperationException("No automatic startup attempt is available.");
        FailedStartupAttempts++;
    }

    public void StartupSucceeded()
    {
        FailedStartupAttempts = 0;
        Recovering = false;
    }
}
