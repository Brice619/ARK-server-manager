namespace ArkServerManager.Core;

public sealed record AppUpdateLaunchOutcome(bool Updated, string Notice, string? RecoveryJournal);
