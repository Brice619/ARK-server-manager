using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ArkServerManager.Core;

/// <summary>A single-user, single-owner transport for a privileged snapshot helper.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsSnapshotPipe
{
    public static string NewName() => "ark-snapshot-" + Guid.NewGuid().ToString("N");

    public static NamedPipeServerStream CreateServer(string name)
    {
        ValidateName(name);
        return new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
    }

    public static async Task<NamedPipeClientStream> ConnectAsync(string name, int ownerProcessId,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        if (ownerProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(ownerProcessId));
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(cancellationToken);
            RequireServer(client, ownerProcessId);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public static void RequireServer(NamedPipeClientStream client, int expectedProcessId)
    {
        if (expectedProcessId <= 0 || !GetNamedPipeServerProcessId(client.SafePipeHandle, out var actual)
            || actual != expectedProcessId)
            throw new IOException("The snapshot request channel belongs to another manager process.");
    }

    public static void RequireClient(NamedPipeServerStream server, int expectedProcessId)
    {
        if (expectedProcessId <= 0 || !GetNamedPipeClientProcessId(server.SafePipeHandle, out var actual)
            || actual != expectedProcessId)
            throw new IOException("The snapshot response channel belongs to another helper process.");
    }

    private static void ValidateName(string name)
    {
        const string prefix = "ark-snapshot-";
        if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal)
            || name.Length != prefix.Length + 32
            || !Guid.TryParseExact(name[prefix.Length..], "N", out var id) || id == Guid.Empty)
            throw new ArgumentException("Choose an owned snapshot channel.", nameof(name));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
