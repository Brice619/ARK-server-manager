using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Core.Tests;

public sealed class ServerAccessHostTests
{
    [Fact]
    public async Task ActualHostAppliesStoppedPasswordPatchRejectsStaleReviewAndReplaysWithoutSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-access-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var directory = Path.Combine(root, "servers", "fixture");
            var windows = OperatingSystem.IsWindows();
            var binary = Path.Combine(directory, "ShooterGame", "Binaries", windows ? "Win64" : "Linux",
                windows ? "ShooterGameServer.exe" : "ShooterGameServer");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            await File.WriteAllTextAsync(binary, "never launched");
            var config = Path.Combine(directory, "ShooterGame", "Saved", "Config", windows ? "WindowsServer" : "LinuxServer", "GameUserSettings.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            await File.WriteAllTextAsync(config, "; preserve me\n[ServerSettings]\nServerAdminPassword=old-admin\nServerPassword=old-join\nRCONEnabled=True\nRCONPort=32330\n[Fixture]\nOption=keep\n");
            var id = Guid.NewGuid();
            await new CatalogRepository(Path.Combine(root, "catalog.json")).ChangeAsync(c => c.AddServer(new(id,
                ArkEdition.ASE, "TheIsland", directory, new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 })));
            var hostDirectory = Path.Combine(AppContext.BaseDirectory, "host-fixture");
            await using var host = ManagedProcess.Start(new(Path.Combine(hostDirectory, windows ? "ArkServerManager.Host.exe" : "ArkServerManager.Host"),
                hostDirectory, ["--data-root", root], Path.Combine(root, "host.log")));
            var client = new LocalManagerClient(root);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                try { await client.CallAsync<JsonElement>("ping", null, TimeSpan.FromMilliseconds(300), deadline.Token); break; }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException && !deadline.IsCancellationRequested)
                { if (!host.IsAlive) throw new IOException("Fixture host exited."); await Task.Delay(50, deadline.Token); }
            }
            var review = await client.CallAsync<ServerAccessReview>("server-access-read", id, TimeSpan.FromSeconds(5), deadline.Token);
            Assert.True(review.AdministratorConfigured); Assert.True(review.JoinPasswordConfigured);
            Assert.DoesNotContain("old-admin", JsonSerializer.Serialize(review));
            var requestId = Guid.NewGuid();
            var selection = new ServerAccessSave(new(review, "new-private-admin", ""), SaveChoice.SaveForNextStart, true);
            var started = await client.CallAsync<BackgroundOperation>("server-access-save-start", selection, TimeSpan.FromSeconds(5), deadline.Token, requestId);
            var finished = await Finish(requestId);
            Assert.Equal(BackgroundOperationState.Succeeded, finished.State);
            var result = finished.Result!.Value.Deserialize<ServerAccessSaveResult>()!;
            Assert.Equal(id, result.ServerId); Assert.True(result.Save.Saved); Assert.False(result.Save.Pending); Assert.False(result.Save.Restarted);
            Assert.DoesNotContain("new-private-admin", JsonSerializer.Serialize(finished));
            var text = await File.ReadAllTextAsync(config);
            Assert.Contains("ServerAdminPassword=new-private-admin", text);
            Assert.DoesNotContain("ServerPassword=old-join", text);
            Assert.Contains("; preserve me", text); Assert.Contains("Option=keep", text);
            var replay = await client.CallAsync<BackgroundOperation>("server-access-save-start", selection, TimeSpan.FromSeconds(5), deadline.Token, requestId);
            Assert.Equal(started.CreatedAt, replay.CreatedAt); Assert.Equal(BackgroundOperationState.Succeeded, replay.State);
            var staleId = Guid.NewGuid();
            await client.CallAsync<BackgroundOperation>("server-access-save-start", selection, TimeSpan.FromSeconds(5), deadline.Token, staleId);
            Assert.Equal(BackgroundOperationState.Failed, (await Finish(staleId)).State);
            Assert.Equal(text, await File.ReadAllTextAsync(config));
            var refreshed = await client.CallAsync<ServerAccessReview>("server-access-read", id, TimeSpan.FromSeconds(5), deadline.Token);
            Assert.True(refreshed.AdministratorConfigured); Assert.False(refreshed.JoinPasswordConfigured); Assert.Null(refreshed.PendingId);
            var schedule = await client.CallAsync<DailyRestartSchedule>("server-restart-schedule-read", id, TimeSpan.FromSeconds(5), deadline.Token);
            Assert.False(schedule.Enabled);
            var scheduleId = Guid.NewGuid();
            var scheduleEdit = new DailyRestartSave(id, new(4, 15), true, true);
            var scheduleJob = await client.CallAsync<BackgroundOperation>("server-restart-schedule-save-start", scheduleEdit, TimeSpan.FromSeconds(5), deadline.Token, scheduleId);
            Assert.Equal(BackgroundOperationState.Succeeded, (await Finish(scheduleId)).State);
            var savedSchedule = await client.CallAsync<DailyRestartSchedule>("server-restart-schedule-read", id, TimeSpan.FromSeconds(5), deadline.Token);
            Assert.True(savedSchedule.Enabled); Assert.Equal(new TimeOnly(4, 15), savedSchedule.Time);
            Assert.Equal(scheduleJob.CreatedAt, (await client.CallAsync<BackgroundOperation>("server-restart-schedule-save-start", scheduleEdit,
                TimeSpan.FromSeconds(5), deadline.Token, scheduleId)).CreatedAt);
            Assert.Equal(text, await File.ReadAllTextAsync(config));

            async Task<BackgroundOperation> Finish(Guid job)
            {
                while (true)
                {
                    var status = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(job), TimeSpan.FromSeconds(5), deadline.Token);
                    if (status.FinishedAt is not null) return status;
                    await Task.Delay(25, deadline.Token);
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
