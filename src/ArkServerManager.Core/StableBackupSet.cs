namespace ArkServerManager.Core;

/// <summary>Several source directories protected by one coordinated capture lifetime.</summary>
public interface IStableBackupSet : IAsyncDisposable
{
    SnapshotCapture Capture { get; }
    // Returned views borrow the set's lifetime. Disposing a view must not release sibling views.
    IStableServerState GetState(string sourceDirectory);
}
