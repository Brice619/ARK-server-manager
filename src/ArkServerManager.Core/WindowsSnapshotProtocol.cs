namespace ArkServerManager.Core;

public sealed record WindowsSnapshotRequest(int SchemaVersion, List<string> Volumes);
public sealed record WindowsSnapshotVolume(string Volume, Guid SnapshotId, string DeviceObject);
public sealed record WindowsSnapshotResponse(int SchemaVersion, SnapshotCapture Capture, List<WindowsSnapshotVolume> Volumes);
public sealed record WindowsSnapshotCapability(bool Windows, bool Elevated, bool ProviderLoaded, string Notice);
public sealed record WindowsSnapshotReleaseResponse(int SchemaVersion, Guid CaptureId);
