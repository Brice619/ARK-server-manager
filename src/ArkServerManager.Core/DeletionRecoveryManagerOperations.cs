using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record DeletionRecoveryResult(Guid OperationId, Guid ServerId, ServerDeletionPhase Phase);

public sealed class DeletionRecoveryManagerOperations(CatalogRepository repository, string managementRoot,
    BackgroundOperations operations,
    Func<ServerDeletionRecord, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedFiles)
{
    public object Start(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<DeletionRecoverySelection>()
            ?? throw new ArgumentException("Choose a recovery notice.");
        if (selection.OperationId == Guid.Empty || selection.ServerId == Guid.Empty
            || string.IsNullOrWhiteSpace(selection.ConfirmedName)
            || selection.Action is not (DeletionRecoveryAction.RestoreStagedFiles or DeletionRecoveryAction.FinishFileRemoval))
            throw new ArgumentException("Review and confirm a supported recovery action.");
        return operations.Submit(request.Id, "deletion-recovery", request.Payload, async (progress, token) =>
        {
            progress(new("Checking recovery details and stopped server files."));
            var record = await ServerDeletionRecovery.RecoverAsync(repository, managementRoot, selection.OperationId,
                withStoppedFiles, token, selection);
            return new DeletionRecoveryResult(record.OperationId, record.ServerId, record.Phase);
        });
    }
}
