namespace ArkServerManager.Core;

public sealed record BackupHistoryRequest(Guid ServerId);
public sealed record ClusterBackupHistoryRequest(Guid ClusterId);
