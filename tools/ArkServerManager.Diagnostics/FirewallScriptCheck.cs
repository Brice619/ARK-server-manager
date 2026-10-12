using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ArkServerManager.Core;

internal static class FirewallScriptCheck
{
    public static async Task<object> RunAsync(string evidence, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(evidence)) throw new ArgumentException("Choose an absolute Windows evidence path.");
        if (Path.Exists(evidence)) throw new IOException("Choose a new evidence directory.");
        Directory.CreateDirectory(evidence);
        var server = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(evidence, "server"),
            new() { Name = "Firewall verification", GamePort = 7777, QueryPort = 27015, RconPort = 32330 });
        var plan = WindowsServerFirewallPlan.Build(server, ServerNetworkScope.LocalNetwork);
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var results = new List<object>();
        foreach (var scenario in new[] { "correct", "ports", "program", "scope", "disabled", "removed", "removal-remains" })
        {
            var removing = scenario.StartsWith("remov", StringComparison.Ordinal);
            var preamble = $$"""
                $global:present = $true
                $ProgressPreference = 'SilentlyContinue'
                $global:fixtureProgram = {{Quote(scenario == "program" ? plan.Program + ".replacement" : plan.Program)}}
                function Get-NetFirewallRule {
                    [CmdletBinding()] param($Name, $PolicyStore)
                    if ($global:present) { [pscustomobject]@{ Group = 'ARK Server Manager'; Enabled = '{{(scenario == "disabled" ? "False" : "True")}}'; Direction = 'Inbound'; Action = 'Allow'; Profile = 'Any' } }
                }
                function Get-NetFirewallApplicationFilter {
                    [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject)
                    process { [pscustomobject]@{ Program = $global:fixtureProgram } }
                }
                function Get-NetFirewallPortFilter {
                    [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject)
                    process { [pscustomobject]@{ Protocol = 'UDP'; LocalPort = @('7777','7778','{{(scenario == "ports" ? "32330" : "27015")}}') } }
                }
                function Get-NetFirewallAddressFilter {
                    [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject)
                    process { [pscustomobject]@{ RemoteAddress = @('{{(scenario == "scope" ? "Any" : "LocalSubnet")}}') } }
                }
                function Set-NetFirewallRule {
                    [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject,$Direction,$Action,$Enabled,$Profile,$Protocol,$LocalPort,$RemoteAddress)
                    process { }
                }
                function New-NetFirewallRule { throw 'Unexpected native creation branch in this fixture.' }
                function Remove-NetFirewallRule {
                    [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject)
                    process { {{(scenario == "removal-remains" ? "" : "$global:present = $false")}} }
                }
                """;
            var script = preamble + "\ntry {\n" + (removing ? WindowsServerFirewallPlan.RemovalScript(server)
                : WindowsServerFirewallPlan.CreateScript(server, ServerNetworkScope.LocalNetwork))
                + "\nexit 0\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 3 }";
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("PowerShell simulation did not start.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { if (!process.HasExited) process.Kill(); throw; }
            var expectedExit = scenario is "correct" or "removed" ? 0 : 3;
            var expectedError = scenario switch
            {
                "ports" => "Firewall player ports verification failed.",
                "program" => "The rule belongs to a different executable.",
                "scope" => "Firewall remote scope verification failed.",
                "disabled" => "Firewall rule verification failed.",
                "removal-remains" => "Firewall removal verification failed.",
                _ => ""
            };
            var error = await errors;
            if (process.ExitCode != expectedExit || expectedExit != 0 && !error.Contains(expectedError, StringComparison.Ordinal))
                throw new IOException("Firewall simulation failed: " + scenario + ": " + error + await output);
            results.Add(new { Scenario = scenario, process.ExitCode, ExpectedExit = expectedExit, Error = error.Trim() });
        }
        var report = new { RealFirewallChanged = false, ScriptSimulationPassed = true, Results = results };
        await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
        return report;
    }
}
