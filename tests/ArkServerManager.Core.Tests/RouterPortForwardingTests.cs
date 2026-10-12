using System.Net;
using Mono.Nat;
using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class RouterPortForwardingTests
{
    private sealed class Router : INatDevice
    {
        public IPEndPoint DeviceEndpoint => new(IPAddress.Parse("192.168.1.1"), 1900);
        public DateTime LastSeen => DateTime.UtcNow;
        public NatProtocol NatProtocol => NatProtocol.Upnp;
        public List<Mapping> Mappings = [];
        public int? FailPort;
        public Task<Mapping[]> GetAllMappingsAsync() => Task.FromResult(Mappings.ToArray());
        public Task<Mapping> CreatePortMapAsync(Mapping mapping)
        {
            if (mapping.PublicPort == FailPort) throw new IOException("Fixture failure");
            Mappings.Add(mapping); return Task.FromResult(mapping);
        }
        public Task<Mapping> DeletePortMapAsync(Mapping mapping) { Mappings.Remove(mapping); return Task.FromResult(mapping); }
        public Task<IPAddress> GetExternalIPAsync() => Task.FromResult(IPAddress.Parse("203.0.113.1"));
        public Task<Mapping> GetSpecificMappingAsync(Protocol protocol, int port)
            => Task.FromResult(Mappings.Single(mapping => mapping.Protocol == protocol && mapping.PublicPort == port));
    }
    private static ServerDefinition Server() => new(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.GetTempPath(),
        new() { Name = "Test", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
    [Fact]
    public async Task SuccessfulSetupCheckpointsBeforeEachMutationAndExcludesRcon()
    {
        var router = new Router(); var checkpoints = new List<(int, bool)>();
        var result = await RouterPortForwarding.CreateAsync(router, Server(), _ => { }, checkpoint: (port, verified) =>
        { checkpoints.Add((port, verified)); return Task.CompletedTask; });
        Assert.Equal(new[] { 7777, 7778, 27015 }, result.Ports);
        Assert.Equal(new[] { (7777, false), (7777, true), (7778, false), (7778, true), (27015, false), (27015, true) }, checkpoints);
        Assert.All(router.Mappings, mapping => Assert.Equal(Protocol.Udp, mapping.Protocol));
        Assert.DoesNotContain(router.Mappings, mapping => mapping.PublicPort == 32330);
    }
    [Fact]
    public async Task OccupiedPortRefusesEntireSetupWithoutChangingOriginalRule()
    {
        var router = new Router(); var original = new Mapping(Protocol.Udp, 8000, 27015, 0, "Another app"); router.Mappings.Add(original);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RouterPortForwarding.CreateAsync(router, Server(), _ => { }));
        Assert.Same(original, Assert.Single(router.Mappings));
    }
    [Fact]
    public async Task PartialFailureRemovesCreatedRulesAndPreservesUnrelatedRules()
    {
        var router = new Router { FailPort = 7778 }; var original = new Mapping(Protocol.Tcp, 80, 80, 0, "Other"); router.Mappings.Add(original);
        await Assert.ThrowsAsync<IOException>(() => RouterPortForwarding.CreateAsync(router, Server(), _ => { }));
        Assert.Same(original, Assert.Single(router.Mappings));
    }

    [Fact]
    public async Task RemovalCanRecoverUnknownRecordedAttemptAndKeepsOtherMappings()
    {
        var router = new Router(); var server = Server();
        await RouterPortForwarding.CreateAsync(router, server, _ => { });
        var unrelated = new Mapping(Protocol.Tcp, 7777, 7777, 0, "Other application"); router.Mappings.Add(unrelated);
        var record = new RouterSetupRecord(1, Guid.NewGuid(), server.Id, server.Directory, "192.168.1.1", 7777, 27015,
            RouterSetupPhase.NeedsReview, 7777, []);
        await RouterPortForwarding.RemoveAsync(router, record);
        Assert.Same(unrelated, Assert.Single(router.Mappings));
        await RouterPortForwarding.RemoveAsync(router, record);
        Assert.Same(unrelated, Assert.Single(router.Mappings));
    }

    [Fact]
    public async Task RemovalRefusesChangedOwnershipBeforeDeletingAnyRule()
    {
        var router = new Router(); var server = Server();
        await RouterPortForwarding.CreateAsync(router, server, _ => { });
        router.Mappings.RemoveAt(1); router.Mappings.Add(new(Protocol.Udp, 7778, 7778, 0, "Different owner"));
        var record = new RouterSetupRecord(1, Guid.NewGuid(), server.Id, server.Directory, "192.168.1.1", 7777, 27015,
            RouterSetupPhase.Completed, null, [7777, 7778, 27015]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RouterPortForwarding.RemoveAsync(router, record));
        Assert.Equal(3, router.Mappings.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RouterPortForwarding.RemoveAsync(router, record with { Gateway = "192.168.2.1" }));
        Assert.Equal(3, router.Mappings.Count);
    }
}
