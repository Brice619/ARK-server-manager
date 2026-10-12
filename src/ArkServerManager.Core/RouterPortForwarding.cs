using Mono.Nat;

namespace ArkServerManager.Core;
public sealed record RouterForwardingResult(Guid ServerId, string Gateway, IReadOnlyList<int> Ports);
public static class RouterPortForwarding
{
    public static async Task<RouterForwardingResult> CreateAsync(INatDevice router, ServerDefinition server,
        Action<OperationProgress> progress, CancellationToken ct = default, Func<int, bool, Task>? checkpoint = null)
    {
        if (router.NatProtocol != NatProtocol.Upnp) throw new NotSupportedException("This router needs manual forwarding setup.");
        server.Settings.Validate();
        var ports = new[] { server.Settings.GamePort, server.Settings.GamePort + 1, server.Settings.QueryPort };
        var existing = await router.GetAllMappingsAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (existing.Any(mapping => mapping.Protocol == Protocol.Udp && ports.Contains(mapping.PublicPort)))
            throw new InvalidOperationException("A requested router port is already forwarded. Review its existing rule before changing it.");
        var created = new List<Mapping>();
        try
        {
            foreach (var port in ports)
            {
                ct.ThrowIfCancellationRequested();
                progress(new("Forwarding player UDP port " + port + "."));
                var expected = new Mapping(Protocol.Udp, port, port, 0, Description(server.Id, port));
                if (checkpoint is not null) await checkpoint(port, false);
                // Observe the accepted mutation to completion; cancellation must not turn it into a retry.
                var actual = await router.CreatePortMapAsync(expected);
                created.Add(actual);
                if (actual.Protocol != Protocol.Udp || actual.PublicPort != port || actual.PrivatePort != port)
                    throw new IOException("The router selected a different forwarding port. Setup was not accepted.");
                var verified = await router.GetSpecificMappingAsync(Protocol.Udp, port).WaitAsync(TimeSpan.FromSeconds(10));
                if (!Owned(verified, expected)) throw new IOException("The new router mapping could not be verified.");
                if (checkpoint is not null) await checkpoint(port, true);
            }
            return new(server.Id, router.DeviceEndpoint.Address.ToString(), ports);
        }
        catch (Exception failure)
        {
            var cleanupErrors = new List<Exception>();
            foreach (var mapping in created.AsEnumerable().Reverse())
            {
                try
                {
                    var current = await router.GetSpecificMappingAsync(mapping.Protocol, mapping.PublicPort).WaitAsync(TimeSpan.FromSeconds(10));
                    if (!Owned(current, mapping)) throw new IOException("A created mapping changed and needs manual review.");
                    await router.DeletePortMapAsync(current);
                }
                catch (Exception error) { cleanupErrors.Add(error); }
            }
            if (cleanupErrors.Count > 0) throw new AggregateException("Router setup failed and some rules need manual review.", new[] { failure }.Concat(cleanupErrors));
            throw;
        }
    }
    private static string Description(Guid id, int port) => "ARKServerManager-" + id.ToString("N") + "-" + port;
    public static async Task RemoveAsync(INatDevice router, RouterSetupRecord record, CancellationToken ct = default)
    {
        if (router.NatProtocol != NatProtocol.Upnp || router.DeviceEndpoint.Address.ToString() != record.Gateway)
            throw new InvalidOperationException("The recorded router was not found. Review its rules manually.");
        var ports = new[] { record.GamePort, record.GamePort + 1, record.QueryPort };
        var mappings = await router.GetAllMappingsAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);
        var selected = mappings.Where(mapping => mapping.Protocol == Protocol.Udp && ports.Contains(mapping.PublicPort)).ToArray();
        foreach (var mapping in selected)
            if (!Owned(mapping, new(Protocol.Udp, mapping.PublicPort, mapping.PublicPort, 0, Description(record.ServerId, mapping.PublicPort))))
                throw new InvalidOperationException("A recorded port now belongs to another rule. Nothing was removed; review the router manually.");
        foreach (var mapping in selected)
        {
            ct.ThrowIfCancellationRequested();
            var current = await router.GetSpecificMappingAsync(Protocol.Udp, mapping.PublicPort).WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (!Owned(current, mapping)) throw new InvalidOperationException("A router rule changed during removal. Remaining rules need review.");
            await router.DeletePortMapAsync(current);
        }
        var remaining = await router.GetAllMappingsAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (remaining.Any(mapping => mapping.Protocol == Protocol.Udp && ports.Contains(mapping.PublicPort)
            && mapping.Description == Description(record.ServerId, mapping.PublicPort)))
            throw new IOException("Some app router rules remain. Retry removal after reviewing them.");
    }
    private static bool Owned(Mapping current, Mapping expected) => current.Protocol == expected.Protocol
        && current.PublicPort == expected.PublicPort && current.PrivatePort == expected.PrivatePort && current.Description == expected.Description;
}
