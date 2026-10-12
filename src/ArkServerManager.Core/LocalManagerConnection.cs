using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ManagerRequest(int ProtocolVersion, Guid Id, string Operation, JsonElement? Payload);
public sealed record ManagerResponse(int ProtocolVersion, Guid Id, bool Success, JsonElement? Data, string? Error);

public static class LocalManagerProtocol
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 32 };
    public static string PipeName(string dataRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (OperatingSystem.IsWindows()) root = root.ToUpperInvariant();
        var identity = Environment.UserName + "\n" + root;
        return "ark-manager-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Manager message exceeds the size limit.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size is < 1 or > MaximumMessageBytes) throw new InvalidDataException("Invalid manager message length.");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("Empty manager message.");
    }
}

public sealed class LocalManagerClient(string dataRoot)
{
    public async Task<T> CallAsync<T>(string operation, object? payload, TimeSpan timeout, CancellationToken ct = default, Guid? requestId = null)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (requestId == Guid.Empty) throw new ArgumentException("A request identity cannot be empty.", nameof(requestId));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        await using var pipe = new NamedPipeClientStream(".", LocalManagerProtocol.PipeName(dataRoot), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(token);
            var request = new ManagerRequest(LocalManagerProtocol.Version, requestId ?? Guid.NewGuid(), operation,
                payload is null ? null : JsonSerializer.SerializeToElement(payload));
            await LocalManagerProtocol.WriteAsync(pipe, request, token);
            var response = await LocalManagerProtocol.ReadAsync<ManagerResponse>(pipe, token);
            if (response.ProtocolVersion != LocalManagerProtocol.Version || response.Id != request.Id)
                throw new InvalidDataException("Unexpected manager response identity or protocol version.");
            if (!response.Success) throw new InvalidOperationException(response.Error ?? "Manager request failed.");
            if (response.Data is not JsonElement data) throw new InvalidDataException("Manager response has no data.");
            var value = data.Deserialize<T>();
            if (value is null) throw new InvalidDataException("Manager response data is empty.");
            return value;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("The background manager did not respond in time."); }
    }
}

public sealed class LocalManagerServer(string dataRoot, Func<ManagerRequest, CancellationToken, Task<object>> handle,
    Func<Task>? beforeOwnershipRelease = null, Func<ManagerRequest, Task>? afterResponse = null,
    Func<CancellationToken, Task>? afterOwnershipAcquired = null)
{
    public Exception? LastRequestFailure { get; private set; }
    public async Task RunAsync(CancellationToken ct)
    {
        var root = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(root);
        await using var ownership = new FileStream(Path.Combine(root, "manager-host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (afterOwnershipAcquired is not null) await afterOwnershipAcquired(ct);
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => AcceptLoopAsync(ct)));
        }
        finally { if (beforeOwnershipRelease is not null) await beforeOwnershipRelease(); }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(LocalManagerProtocol.PipeName(dataRoot), PipeDirection.InOut, 4,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var requestId = Guid.Empty;
            try
            {
                var request = await LocalManagerProtocol.ReadAsync<ManagerRequest>(pipe, deadline.Token);
                requestId = request.Id;
                if (request.ProtocolVersion != LocalManagerProtocol.Version || request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Operation))
                    throw new InvalidDataException("Invalid manager request identity or protocol version.");
                var data = await handle(request, deadline.Token);
                try
                {
                    await LocalManagerProtocol.WriteAsync(pipe, new ManagerResponse(LocalManagerProtocol.Version, requestId, true,
                        JsonSerializer.SerializeToElement(data), null), deadline.Token);
                }
                finally { if (afterResponse is not null) await afterResponse(request); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                LastRequestFailure = error;
                var message = error is IOException or JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException
                    ? error.Message : "The background operation failed.";
                try
                {
                    await LocalManagerProtocol.WriteAsync(pipe, new ManagerResponse(LocalManagerProtocol.Version, requestId, false, null, message), deadline.Token);
                }
                catch (Exception sendError) when (sendError is IOException or OperationCanceledException) { }
            }
        }
    }
}
